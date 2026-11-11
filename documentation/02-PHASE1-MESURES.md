# Phase 1 — Mesures réelles

Toutes les valeurs ci-dessous sont **mesurées** sur la machine cible le 8 novembre 2026,
via `tools/Phase1Probe/NightshiftProbe.cs` (compilé avec le compilateur .NET Framework x64,
exécuté en 64 bits). Rien n'est extrapolé.

---

## 1. Système

| Élément | Valeur |
|---|---|
| Produit / build | Windows 11, **26200.9711** (25H2) |
| Architecture | x64 |
| GPU | AMD Radeon(TM) Graphics, pilote 31.0.21912.3005 |
| Mode d'affichage | 1600 × 900 |
| Runtimes .NET | Microsoft.NETCore.App 8.0.5, 9.0.8 ; WindowsDesktop.App 8.0.5, 9.0.8 |
| SDK .NET | **aucun** (`dotnet --list-sdks` vide) → à installer |

> Note : `Environment.OSVersion` rapporte `6.2.9200` (manifeste legacy) ; le build réel vient du registre.

---

## 2. Écrans et gamma ramps (lecture seule)

```
device=\\.\DISPLAY1 primary=True
  adapter=AMD Radeon(TM) Graphics
  monitor=Generic PnP Monitor
  monitor_id=MONITOR\LEN9080\{4d36e96e-e325-11ce-bfc1-08002be10318}\0000
  GetDeviceGammaRamp=True
  ramp_identity=False ramp_monotonic=True
  samples R[0,128,255]=0,32896,65535  G=0,32896,65535  B=0,32896,65535
count_displays=1
```

**Interprétation :**
- La LUT est **lisible** → la voie GDI est ouverte sur cette machine.
- `ramp_identity=False` **ne signifie pas** qu'un filtre est actif : les trois canaux sont **identiques** et la valeur en i=128 vaut 32896 = 128×257, c'est-à-dire une échelle 0→65535 sur 255 pas (ramp « neutre » du pilote) au lieu de 128×256. Le moteur ne doit donc **jamais** comparer à une identité codée en dur : il compare à la ramp capturée au démarrage (tolérance) — ce point est intégré à l'architecture (§8).
- Aucune correction ICC/constructeur significative, aucun filtre tiers en cours.

---

## 3. Test d'écriture réel (`--tint`)

```
device=\\.\DISPLAY1 kelvin=4500 Set=True readback_ok=True max_delta=0
device=\\.\DISPLAY1 kelvin=2700 Set=True readback_ok=True max_delta=0
device=\\.\DISPLAY1 RESTORE Set=True restored_exactly=True
```

**Interprétation :**
- `SetDeviceGammaRamp` **retourne True** et le pilote **conserve exactement** la table écrite (relecture identique) → filtre réellement appliqué, pas simulé.
- **2700 K accepté** : le clamp anti-malware de Windows ne rejette pas une rampe correspondant au besoin du cahier des charges (2000–2700 K).
- **Restauration bit-exacte vérifiée** (`restored_exactly=True`) : c'est la garantie la plus importante du produit, validée dès la Phase 1.
- Les deux températures ont été visibles à l'œil pendant ~1,2 s chacune ; l'écran a été restauré immédiatement après.

---

## 4. Luminosité

### DDC/CI (moniteurs externes)
```
hmonitor#1 name="Generic PnP Monitor" ddc_brightness=False err=-1071241847
```
`0xC0262589` = `ERROR_GRAPHICS_I2C_ERROR_TRANSMITTING_DATA` : la dalle interne ne répond pas au DDC/CI.
→ Le backend DDC/CI sera implémenté (indispensable pour les moniteurs externes) mais **n'est pas disponible sur cette machine** : il ne pourra pas être validé ici, seulement sur un écran externe. À déclarer honnêtement comme « implémenté, non validé sur cette configuration ».

### WMI (dalles internes)
```
WmiMonitorBrightness : Current=50, Levels=0..100
WmiMonitorBrightnessMethods : DISPLAY\LEN9080\5&139b7f7a&0&UID256_0
```
→ **Contrôle matériel du rétroéclairage disponible** via `SetBrightness`. C'est le backend prioritaire ici.

### Conclusion luminosité
Sur cette machine : **WMI = rétroéclairage réel**, DDC/CI = indisponible, **atténuation gamma** en repli universel.

---

## 5. Night Light de Windows

La clé `HKCU\...\CloudStore\...\bluelightreductionstate` est **présente**.
→ L'utilisateur a déjà utilisé le Night Light : la coexistence doit être gérée explicitement (détection + avertissement + préférence), pas ignorée.

---

## 6. Reproductibilité

```
cd NIGHTSHIFT/tools/Phase1Probe
"/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe" -nologo -target:exe -platform:x64 -optimize+ -out:NightshiftProbe.exe NightshiftProbe.cs
./NightshiftProbe.exe            # lecture seule
./NightshiftProbe.exe --tint     # écriture + restauration contrôlée
./NightshiftProbe.exe --restore ramps
```

Les ramps d'origine sont sauvegardées dans `tools/Phase1Probe/ramps/*.ramp` à chaque exécution
et peuvent être restaurées indépendamment par `--restore`.
