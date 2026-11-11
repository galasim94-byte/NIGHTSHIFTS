# Phase 2 — Moteur de température : résultats mesurés

Date : 8 novembre 2026 — Machine : Windows 11 26200.9711, AMD Radeon iGPU, dalle LEN9080 1600×900.

Tout ce qui suit a été **exécuté sur le matériel réel**, pas simulé. Reproductible via
`tools/EngineHarness`.

---

## 1. Ce qui est livré

```
src/Core/            Nightshift.Core          (aucun Win32, testable sans écran)
  Color/ColorTemperature.cs    CCT → point blanc sRGB (lieu de Planck, Kim et al. + matrice sRGB)
  Color/RampBuilder.cs         LUT 3×256, lumière linéaire, fusion température + luminosité, Blend
  Color/RampMath.cs            deltas, neutralité, monotonie, valeurs témoins
  Transitions/TransitionEngine.cs  interpolation déterministe (horloge injectée, smootherstep)

src/DisplayEngine/   Nightshift.DisplayEngine (seule couche d'interop)
  Native/Gdi32.cs, User32.cs, Dxva2.cs, Dxgi.cs
  Displays/DisplayEnumerator.cs
  Gamma/GdiGammaController.cs       snapshot, application vérifiée, restauration, plafond de conformité
  DisplayFilterEngine.cs            orchestration multi-écrans, transitions, luminosité, restauration
  Brightness/                       WMI (dalle interne), DDC/CI (externe), sélection automatique
  Hdr/HdrProbe.cs                   état HDR/Advanced Color par sortie (DXGI 1.6)
  Storage/RampSnapshotStore.cs      snapshot disque + drapeau « restauration en attente »

tools/EngineHarness/  harnais console (list, set, fade, brightness, selftest, hdrdebug)
tests/Nightshift.Core.Tests/  28 tests unitaires
```

---

## 2. Vérifications exécutées

### 2.1 Compilation
`dotnet build NIGHTSHIFT.sln -c Release` → **0 erreur, 0 avertissement**.

### 2.2 Tests unitaires
`dotnet test` → **28/28 réussis** (science des couleurs, LUT, bornage, comparaisons, transitions).

### 2.3 Selftest moteur sur écran réel

| Consigne | Appliqué | R255 | G255 | B255 | Intensité réellement obtenue |
|---|---|---|---|---|---|
| 6500 K | oui | 65535 | 65535 | 65535 | 100 % |
| 5000 K | oui | 65535 | 58962 | 53096 | 100 % |
| 4000 K | oui | 65535 | 53992 | 42064 | 100 % |
| 3500 K | oui | 65535 | 50792 | 35465 | 100 % |
| 3000 K | oui | 65535 | 46879 | 28027 | **80 %** |
| 2700 K | oui | 65535 | 44049 | 23011 | **70 %** |
| 2200 K | oui | 65535 | 38170 | 12991 | **60 %** |
| 2000 K | oui | 65535 | 35291 | 7268 | **50 %** |

- Chaque écriture est vérifiée par relecture : **`delta=0`** partout (le pilote conserve exactement la table).
- Bleu décroissant avec la température : **oui**. Luminance décroissante sans perte brutale : **oui**.
- **Restauration exacte : oui.**

### 2.4 Transition réelle
`fade 6500 2700 1500` → **1532 ms** pour 1500 ms demandés, **48 paliers**, courbe **monotone**,
valeur finale **2700 K**, intensité 70 %, **restauration exacte**.

### 2.5 HDR / Advanced Color
`\\.\DISPLAY1` : `BitsPerColor=8`, `ColorSpace=0 (sRGB SDR)`, `MaxLuminance=270`, coordonnées 1600×900.
→ détection **opérationnelle**, cet écran est SDR (la suspension HDR reste donc non exercée sur cette machine).

### 2.6 Luminosité
Backend sélectionné : **WMI (rétroéclairage réel)**, lecture OK (niveau 70/100 à cet instant).

---

## 3. Découvertes techniques (toutes mesurées)

### 3.1 Windows plafonne l'écart à la table neutre — c'est le fait majeur

Avec une conversion colorimétrique **exacte**, 3000 K, 2700 K, 2200 K et 2000 K sont **refusés**
par `SetDeviceGammaRamp` (`FALSE`, sans code d'erreur). 3500 K et au-dessus passent intégralement.

Ce n'est pas notre calcul qui est faux : la conversion Planck → sRGB donne pour 2700 K le blanc
réel d'un corps noir (R=1, G≈0.42, B≈0.10), bien plus saturé que les approximations type
« Tanner Helland » (B≈0.34). La sonde de Phase 1 passait à 2700 K précisément parce que sa table
était plus douce — l'affirmation « 2700 K passe » de l'architecture initiale était donc
**incomplète et a été corrigée**.

Solution retenue, **sans modifier le système** : plafond de conformité. Le moteur descend par paliers
(100 %, 90 %, …) jusqu'à la plus forte intensité acceptée, puis l'expose (`intensité=70 %`).
Les tentatives refusées ne changent rien à l'écran : **aucun scintillement**, une seule image
intermédiaire au plus. Aucune clé de registre, aucune élévation de privilèges, aucun contournement.

Conséquence produit, à afficher honnêtement dans l'UI : sur cette machine, la température
la plus chaude réellement atteignable est un 2000 K **atténué à 50 %**, soit environ 3000 K perçu.
L'UI devra montrer la température demandée **et** l'intensité effective.

### 3.2 `IID_IDXGIOutput6` : un identifiant faux renvoie `E_NOINTERFACE`

Premier essai avec `068346e8-aaec-4b1c-add7-a137f27137a2` → `0x80004002`.
Identifiant correct : **`068346e8-aaec-4b84-add7-137f513f77a1`**. Sans ce détail, la détection HDR
échoue silencieusement et l'application croit que tout va bien.

### 3.3 Décalage de vtable sur ce runtime DXGI

La numérotation théorique place `GetDesc1` au slot 28 ; sur ce runtime, le slot 28 est
`CheckHardwareCompositionSupport` (il n'écrit que 4 octets : `0x00000003` = FULLSCREEN|WINDOWED),
et `GetDesc1` est au **slot 27`. Vérifié par vidage mémoire brut avant correction : le slot 27
écrit bien `\\.\DISPLAY1` + coordonnées 1600×900.
Leçon appliquée : ne jamais se fier à un calcul de vtable non vérifié empiriquement.

### 3.4 Une structure COM non blittable corrompait une référence managée

`DXGI_OUTPUT_DESC1` contenait un `string` : passer un pointeur vers cette structure laissait DXGI
écrire 64 octets sur une référence managée. Corrigé en `fixed char[32]` (structure blittable).
Les trois avertissements `CS8500` ont disparu avec le bug.

### 3.5 La table « neutre » du pilote n'est pas l'identité

Confirmé : `i×257` et non `i×256`. Toute comparaison se fait donc contre la table capturée
au démarrage, jamais contre une identité codée en dur.

---

## 4. Limites restantes (état réel, pas promesse)

| Point | État |
|---|---|
| HDR actif | détection OK ; **suspension automatique non testée** (aucun écran HDR sur cette machine) |
| DDC/CI externe | implémenté, **non validable ici** (dalle interne sans DDC/CI) |
| Gardien externe (`Nightshift.Guardian`) | prévu en Phase 3 ; actuellement : snapshot disque + drapeau `restore.pending` + restauration au démarrage |
| Multi-écrans | code par écran en place, **testé avec un seul écran seulement** |
| Jeux plein écran | détection prévue Phase 7 |
| WPF / UI | non commencé (Phase 3) |

---

## 5. Prochaine étape : Phase 3 (interface)

Fenêtre WPF compacte, chrome sombre sur mesure, état/température/curseur, puis intégration du moteur
déjà validé. La température effective et l'intensité doivent être visibles dès le premier écran,
puisque 3.1 montre qu'elles peuvent différer de la consigne.
