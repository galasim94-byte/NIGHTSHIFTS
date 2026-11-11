# NIGHTSHIFT — Architecture technique

**Phase 1 : analyse réelle de Windows 11 + choix d'architecture**
Date : 8 novembre 2026 — Machine de référence : voir `02-PHASE1-MESURES.md`

---

## 1. Décision en une page

| Sujet | Décision |
|---|---|
| Moteur de filtrage | **Gamma ramps matérielles par écran** via `SetDeviceGammaRamp` (GDI, DC d'affichage). Testé et fonctionnel sur cette machine. |
| Contrôle de luminosité | 3 backends par ordre de priorité : **WMI** (dalles internes) → **DDC/CI** (moniteurs externes) → **atténuation logicielle via gamma** (étiquetée comme telle). |
| UI | **WPF** sur **.NET 8 (LTS)**, fenêtre non empaquetée, chrome sombre personnalisé. |
| Persistance | JSON local dans `%LOCALAPPDATA%\Nightshift`, écriture atomique. |
| Réseau | **Aucun réseau obligatoire.** Lever/coucher du soleil calculés localement (algorithme NOAA). Géolocalisation facultative, opt-in. |
| Élévation | **Aucune.** Ni admin, ni service, ni pilote (vérifié : les gamma ramps ne demandent pas d'élévation). |
| Barre système | `Shell_NotifyIcon` en P/Invoke direct (pas de dépendance WinForms). |
| Raccourcis | `RegisterHotKey` sur une fenêtre message-only. |

Le reste de ce document justifie chaque ligne.

---

## 2. Environnement mesuré (pas supposé)

- Windows 11, build **26200.9711** (25H2), x64.
- Runtimes .NET **8.0.5** et **9.0.8** présents — **aucun SDK installé** (bloquant pour compiler, cf. §14).
- GPU : **AMD Radeon(TM) Graphics** (iGPU), pilote 31.0.21912.3005.
- 1 écran : **1600×900**, dalle interne **LEN9080** (portable Lenovo), exposée comme `Generic PnP Monitor`.
- `GetDeviceGammaRamp` : **OK** (ramp lisible, 3 canaux identiques, monotone).
- `SetDeviceGammaRamp` : **OK** à 4500 K et à 2700 K, relecture conforme, **restauration bit-exacte**.
- DDC/CI luminosité : **indisponible** sur cette dalle (`ERROR_GRAPHICS_I2C_ERROR_TRANSMITTING_DATA`).
- WMI `WmiMonitorBrightness` : **disponible** (niveau 50, échelle 0–100, méthodes `SetBrightness` présentes).
- Night Light Windows : clé CloudStore **présente** → l'utilisateur l'a déjà utilisé, le conflit est réel et doit être géré.

Détail brut et interprétation : `02-PHASE1-MESURES.md`. Reproductible via `tools/Phase1Probe`.

---

## 3. Méthodes de filtrage examinées

### 3.1 Gamma ramp GDI — **RETENUE**

`CreateDC("\\.\DISPLAYn")` + `Get/SetDeviceGammaRamp` (table 3×256 entrées 16 bits).

- Modifie la **LUT matérielle de sortie** de l'écran : tous les contenus (bureau, applications, vidéo, jeux fenêtrés) sont filtrés, sans injection dans les applications.
- Aucune élévation de privilèges, aucune injection de DLL, aucun hook graphique.
- Fonctionne par écran de façon indépendante (`\\.\DISPLAY1`, `\\.\DISPLAY2`, …) → multi-écrans natif.
- Mesuré : accepté, précis, restauration exacte.

**Limites (à assumer et à gérer, pas à cacher) :**
1. **HDR / Advanced Color** : en mode HDR, Windows ignore ou court-circuite la LUT SDR → filtrage inopérant. **Détection obligatoire**, suspension propre, message clair à l'utilisateur.
2. **LUT 8 bits en pratique** : le framebuffer SDR est 8 bits/canal ; la quantification se voit si l'on étire trop le contraste. On ne compense donc pas la luminosité en étirant, on atténue.
3. **Réinitialisation par le système** : changement de résolution/fréquence, reset pilote, sortie de veille, changement de topologie, certains jeux exclusive fullscreen remettent la ramp à zéro. → **veilleur de ramp** qui relit et réapplique (cf. §8).
4. **Plafond Windows** : depuis Windows 10, les ramps qui s'écartent trop de la table neutre sont rejetées (protection anti-malware). **Mesuré en Phase 2** : avec une conversion colorimétrique exacte, la pleine intensité passe jusqu'à 3500 K, puis 3000/2700/2200/2000 K exigent 80/70/60/50 % (la sonde de Phase 1 passait à 2700 K uniquement parce que sa table était plus douce — affirmation corrigée). Parade : **plafond de conformité** qui cherche la plus forte intensité acceptée, la conserve et l'expose à l'UI. Aucun réglage système n'est modifié. Détail : `03-PHASE2-MOTEUR.md`.
5. **Curseur matériel** : généralement non corrigé par la LUT (le pointeur peut rester plus froid). Limite cosmétique documentée.
6. Non persistant après déconnexion : la LUT est remise à l'état du pilote. C'est une **sécurité gratuite** (rien ne reste bloqué après un crash, au pire jusqu'au prochain changement de mode).

### 3.2 DXGI `IDXGIOutput::SetGammaControl` — écartée en v1

Écrit la même LUT matérielle, mais exige de posséder un swapchain plein écran associé à chaque sortie. Le résultat visuel est identique à la voie GDI, avec beaucoup plus de surface de bug (device perdu, alt-tab, HDR). Conservée en **option de repli documentée** si un pilote refusait la voie GDI.

### 3.3 API privée Night Light (`bluesky.dll`, CloudStore) — **rejetée**

Utilisée par certains utilitaires pour piloter le Night Light natif. Non documentée, non supportée, casse à chaque mise à jour, nécessite d'écrire dans le magasin d'état interne de Windows. Contraire à l'exigence « pas de méthodes de contournement dangereuses ».
→ Traitement retenu : **détection** de l'état Night Light (clé CloudStore, surveillance par `RegNotifyChangeKeyValue`) et **résolution de conflit explicite** (avertissement + choix : suspendre NIGHTSHIFT ou désactiver le Night Light).

### 3.4 Overlay fenêtré transparent — rejetée comme méthode principale

Ne modifie pas la sortie : il superpose une couche teintée. Conséquences réelles : noirs délavés, contenu plein écran exclusif non couvert, captures d'écran et partages faussés, double fenêtre par écran, coût DWM permanent. Ce n'est pas un filtre de température de couleur, c'est un voile — exactement ce que le cahier des charges interdit.

### 3.5 Magnification API (`MagSetFullscreenColorEffect`) — rejetée

Effet de couleur plein écran géré par le compositeur, déprécié pour cet usage et incohérent selon les versions/builds ; ne couvre pas les contenus en rendu direct. Aucun avantage par rapport aux gamma ramps ici.

**Conclusion :** la seule méthode à la fois *réelle*, *stable*, *documentée* et *sans privilèges* sur cette machine est la gamma ramp. L'architecture est donc bâtie autour d'elle, avec des garde-fous explicites pour ses 6 limites.

---

## 4. Contrôle de la luminosité

Trois backends, capacité **détectée puis exposée dans l'UI** (jamais supposée) :

| Backend | Cible | État mesuré | Comportement |
|---|---|---|---|
| `WmiMonitorBrightness` / `WmiMonitorBrightnessMethods` (root\WMI) | dalles internes de portable | **OK (50/100)** | rétroéclairage **réel** |
| DDC/CI `SetVCPFeature(0x10)` via dxva2 | moniteurs externes | indisponible ici, implémenté | rétroéclairage **réel** si le moniteur répond |
| Atténuation gamma (multiplicateur sur la LUT) | tout écran | toujours possible | **luminosité perçue**, pas le rétroéclairage — l'UI l'indique explicitement |

Règles :
- Pourcentage affiché = valeur du backend matériel quand il existe, sinon valeur logicielle, avec un badge distinct (`HARDWARE` / `PERÇUE`).
- Si le backend matériel disparaît (écran débranché), bascule automatique et **non silencieuse** vers le logiciel.
- La luminosité logicielle est fusionnée dans la **même** LUT que la température → une seule écriture par écran et par frame, pas deux filtres empilés (pas de cumul de pertes).

---

## 5. Stack UI : WPF plutôt que WinUI 3

| Critère | WinUI 3 | **WPF (retenu)** |
|---|---|---|
| Déploiement sans MSIX | possible mais dégradé (identité de paquet absente → notifications, démarrage, associations limités) | **natif**, exe autonome |
| Runtime supplémentaire | Windows App SDK à embarquer | inclus dans .NET 8 desktop |
| Chrome sombre / design sur mesure | contrôle partiel, tendance à imposer le style Fluent | **contrôle total** (fenêtre sans bordure, DWM `DWMWA_USE_IMMERSIVE_DARK_MODE`) |
| Interop Win32 (tray, hotkeys, DC d'affichage) | possible mais passe par HWND/COM | **direct et mature** |
| Barre système | dépendance tierce (H.NotifyIcon) | `Shell_NotifyIcon` en P/Invoke, zéro dépendance |
| Maturité pour une app residente longue durée | plus jeune | **très éprouvée** |

Le cahier des charges exige une esthétique sur mesure (noir profond, typographie grise, pas de Fluent générique) et une app résidente fiable : WPF est le choix à moindre risque. **.NET 8 LTS** pour la durée de support.

Dépendances prévues : **aucune** au-delà du framework, sauf si un besoin réel apparaît en Phase 3 (`CommunityToolkit.Mvvm` au maximum, pas de framework MVVM lourd, pas de bibliothèque d'icônes).

---

## 6. Architecture modulaire

```
NIGHTSHIFT/
├── NIGHTSHIFT.sln
├── src/
│   ├── Core/            Nightshift.Core.dll      — zéro Win32, 100 % testable
│   ├── DisplayEngine/   Nightshift.DisplayEngine — tout l'interop graphique
│   ├── Scheduler/       Nightshift.Scheduler     — automatisation horaire
│   ├── Settings/        Nightshift.Settings      — persistance JSON
│   ├── Shortcuts/       Nightshift.Shortcuts     — hotkeys globales
│   ├── SystemTray/      Nightshift.SystemTray    — icône de notification
│   ├── UI/              Nightshift.UI            — WPF (vues + viewmodels)
│   └── App/             Nightshift.exe           — composition root + gardien
├── tests/               Nightshift.Core.Tests, Nightshift.DisplayEngine.Tests
├── assets/              icônes, aperçu de paysage, polices embarquées
├── installer/           script Inno Setup (Phase 9)
├── documentation/       ce document + mesures + limites + guide utilisateur
└── tools/Phase1Probe/   sonde de faisabilité (déjà exécutée)
```

**Règle de dépendance stricte :**

```
UI ──▶ App ──▶ { DisplayEngine, Scheduler, Settings, Shortcuts, SystemTray }
                       │
                       ▼
                     Core
```

- `Core` ne référence **rien** (pas même `System.Drawing`) : il produit des **données** (table de LUT `ushort[768]`, profils, planning, calculs solaires). Il est donc testable sans écran, en CI, en millisecondes.
- `DisplayEngine` est la **seule** couche qui parle à `gdi32`/`user32`/`dxva2`/WMI. Elle expose des interfaces (`IGammaController`, `IBrightnessBackend`, `IHdrProbe`) pour être simulable.
- `UI` ne connaît jamais un `HDC` : elle envoie des intentions (`SetTarget(3200)`) au moteur.

### Contenu de `Core`
- `ColorScience` : CCT → chromaticité (lieu de Planck, interpolation CIE 1960 UCS), conversion vers sRGB avec adaptation chromatique (Bradford, D65 → blanc cible), génération de LUT **en lumière linéaire** (gamma 2.2) avec compensation de luminance bornée.
- `RampComposer` : fusion température + luminosité perçue + contraste en une seule table.
- `TransitionEngine` : interpolation temporelle (easing doux, durée configurable, aucune discontinuité de dérivée aux extrémités) ; produit une séquence de LUT.
- `ScheduleEngine` : points-clés horaires + jours de la semaine + interpolation entre points ; calcul du **prochain événement**.
- `SolarCalculator` : lever/coucher **hors ligne** (NOAA), à partir de latitude/longitude.
- `Presets` : NORMAL 6500, SOFT 5000, EVENING 4000, NIGHT 3000, DEEP NIGHT 2200, CUSTOM — tous modifiables.

### Contenu de `DisplayEngine`
- `DisplayEnumerator` : `EnumDisplayDevices` → écrans actifs, nom, primaire, adaptateur, identifiant dalle.
- `GdiGammaController` : un contrôleur par écran, snapshot de la ramp d'origine, application, relecture de vérification.
- `HdrProbe` : état Advanced Color par sortie (DXGI 1.6 `IDXGIOutput6::GetDesc1` / `DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO`) — **implémenté et testé en Phase 2**, c'est le point technique le plus délicat.
- `BrightnessService` : sélection et surveillance des trois backends.
- `DisplayWatcher` : `WM_DISPLAYCHANGE`, `WM_SETTINGCHANGE`, `WM_DPICHANGED`, branchements/débranchements, veille/reprise.
- `FullscreenDetector` : `SHQueryUserNotificationState` + géométrie de la fenêtre au premier plan → suspension auto du filtre (jeux) avec réactivation au retour.
- `RampGuard` : veilleur qui détecte qu'un tiers a écrasé la LUT et réapplique (fréquence adaptative, coût CPU négligeable).

---

## 7. Cycle de vie

**Démarrage** (automatique ou manuel) :
1. Mutex single-instance ; si une instance tourne, on lui envoie « montre la fenêtre » et on quitte.
2. Lecture `settings.json` (validation + migration de schéma). Si invalide → valeurs par défaut, fichier mis en quarantaine.
3. **Contrôle de restauration en attente** : si un snapshot est marqué `dirty` (crash précédent), on restaure la ramp d'origine *avant* toute chose.
4. Énumération des écrans, capture des ramps d'origine, détection HDR / Night Light.
5. Application de l'état cible (profil, mode auto ou manuel) en fondu (par défaut 800 ms).
6. Démarrage du gardien (voir §8) et du veilleur.

**Fonctionnement** : icône de tray, fenêtre cachée ; le moteur tourne sur un timer léger (transition active → ~20 Hz, sinon 0 Hz) ; aucune boucle de rendu.

**Arrêt propre** : `quit` tray, `WM_QUERYENDSESSION`/`WM_ENDSESSION`, exception non gérée, `AppDomain.ProcessExit` → restauration de **toutes** les ramps d'origine, suppression du flag `dirty`, arrêt du gardien.

---

## 8. Garanties de restauration (le point non négociable)

Cinq barrières indépendantes, parce qu'une seule ne suffit jamais :

1. **Snapshot persistant** : ramp d'origine de chaque écran écrite sur disque (clé = identifiant dalle) *avant* la première modification.
2. **Restauration par `finally`** : chaque écriture est encadrée ; toute erreur remet l'original.
3. **Handlers de sortie** : exception non gérée, `ProcessExit`, arrêt de session Windows, arrêt du tray.
4. **Gardien externe** (`Nightshift.Guardian`, ~50 Ko) : lancé avec l'app, il attend la fin du processus principal et, si elle n'est **pas** propre, restaure les ramps depuis le snapshot. Il ne fait rien d'autre, ne lit aucun réglage et meurt ensuite.
5. **Rattrapage au démarrage** : flag `dirty` → restauration avant réapplication.

Application brutale (`TerminateProcess`, coupure de courant) : la LUT peut survivre côté pilote jusqu'au prochain changement de mode ; le gardien et le rattrapage couvrent le cas du kill, le cas « coupure » est couvert par le fait que Windows réinitialise la LUT à la reconnexion de session. **Aucun scénario ne laisse un écran orange après un redémarrage.**

---

## 9. Événements système traités

| Événement | Réaction |
|---|---|
| `WM_DISPLAYCHANGE` (résolution, fréquence, topologie) | ré-énumération, re-capture des originaux si nouveau mode, réapplication |
| Écran débranché / rebranché | contrôleur détruit / recréé, snapshot conservé par dalle |
| Veille / reprise | réapplication complète après `PBT_APMRESUMEAUTOMATIC` |
| Verrouillage de session | filtre **maintenu** (écran de verrouillage inclus) |
| Passage HDR activé | suspension + notification discrète ; réactivation à la sortie du HDR |
| Jeu exclusive fullscreen | suspension auto (réglable), réapplication à la perte du plein écran |
| Night Light modifié par Windows | re-détection, résolution selon la préférence utilisateur |
| Changement de pilote GPU | ré-énumération + re-capture |

---

## 10. Réglages (JSON local)

`%LOCALAPPDATA%\Nightshift\settings.json`, écriture atomique (fichier temporaire + `File.Replace`), `schemaVersion` explicite.

```jsonc
{
  "schemaVersion": 1,
  "enabled": true,
  "mode": "auto",                 // manual | auto
  "targetKelvin": 3400,
  "transitionMs": 800,
  "brightness": { "useHardware": true, "level": 70, "softwareFallbackAllowed": true },
  "presets": [ { "id": "night", "label": "NIGHT", "kelvin": 3000 }, ... ],
  "customPreset": { "kelvin": 3400, "brightness": 75 },
  "schedule": {
    "mode": "keyframes",          // keyframes | solar
    "days": { "mon": true, "...": true },
    "keyframes": [ { "time": "07:00", "kelvin": 6500 }, { "time": "23:00", "kelvin": 2700 } ],
    "latitude": null, "longitude": null
  },
  "suspend": { "untilUtc": null },
  "shortcuts": { "toggle": "Ctrl+Alt+N", "warmer": "Ctrl+Alt+F1", "cooler": "Ctrl+Alt+F2", "night": "Ctrl+Alt+F3", "suspend": "Ctrl+Alt+F4" },
  "behavior": { "startWithWindows": true, "suspendInFullscreen": true, "respectNightLight": "warn" },
  "ramps": { "pendingRestore": false }
}
```

---

## 11. Limites assumées (formulées aux utilisateurs)

- Sur un écran HDR actif, le filtre est **suspendu** : Windows ne permet pas d'altérer la LUT HDR sans API d'affichage avancée (pas d'API publique stable). L'app le dit au lieu de faire semblant.
- Sur beaucoup de moniteurs externes et sur certaines dalles, la **luminosité matérielle** n'est pas pilotable (vérifié ici en DDC/CI). L'app bascule alors sur une atténuation **perçue**, clairement étiquetée.
- Le filtre s'applique à la sortie graphique : il peut être contourné par un jeu exclusive fullscreen qui reprogramme la LUT — d'où la suspension automatique prévue.
- Aucun accès réseau par défaut, aucune télémétrie, aucun fichier système modifié, aucun service, aucune élévation.

---

## 12. Risques et parades

| Risque | Impact | Parade |
|---|---|---|
| HDR activé | filtre inopérant | détection + suspension + message |
| Pilote qui ignore la LUT | filtre inopérant | relecture de contrôle après écriture, statut exposé dans l'UI |
| Plafond Windows sur l'écart à la table neutre | températures très chaudes refusées | plafond de conformité mesuré, intensité effective exposée (100 % jusqu'à 3500 K, 50 % à 2000 K sur la machine de référence) |
| Conflit Night Light | double filtrage, rendu trop chaud | détection + avertissement + choix |
| Crash / kill | écran teinté | gardien + flag `dirty` + rattrapage |
| Multi-écrans hétérogènes | un écran filtré, l'autre non | contrôleur par `DC`, statut par écran |
| Jeux exclusive fullscreen | filtre perdu ou scintillement | suspension auto, réapplication à la sortie |
| SDK .NET absent | pas de compilation | installé en prérequis Phase 2 |

---

## 13. Plan de phases et critères de sortie

| Phase | Contenu | Critère de sortie |
|---|---|---|
| **1. Analyse** | *ce document* + sonde exécutée | ✅ fait |
| **2. Moteur** | `Core` + `DisplayEngine` (gamma, HDR, luminosité, snapshots), harnais console, 28 tests | ✅ **fait** : filtre 6500→2000 K appliqué et vérifié (`delta=0`), transition 1532 ms/1500, restauration exacte, HDR détecté — voir `03-PHASE2-MOTEUR.md` |
| **3. UI** | fenêtre principale, chrome sombre, état/température/curseur | ouverture, réglage, fermeture → écran propre |
| **4. Curseurs & profils** | aperçu temps réel, préréglages éditables | aperçu = rendu réel (comparé) |
| **5. Automatisation** | planning, jours, transitions, suspension | simulation d'horloge testée sans attendre |
| **6. Multi-écrans & tray** | icône, menu contextuel, par écran | débranchement/rebranchement sans artefact |
| **7. Raccourcis & avancé** | hotkeys, plein écran, HDR, démarrage auto | conflits de hotkeys détectés |
| **8. Optimisation & tests** | profil CPU/RAM, scénarios §11 du cahier des charges | tableau de tests rempli, aucun échec connu |
| **9. Installateur** | Inno Setup, exe autonome, désinstalleur propre | installation/désinstallation sur machine vierge |

---

## 14. Prérequis à installer

**SDK .NET 8** (les runtimes 8.0.5 / 9.0.8 sont présents, mais `dotnet --list-sdks` est vide : la compilation WPF est impossible en l'état).

```
winget install Microsoft.DotNet.SDK.8
```

C'est la seule installation système nécessaire. Aucune dépendance NuGet n'est requise pour la Phase 2. Pour la Phase 9 : Inno Setup 6 (`winget install JRSoftware.InnoSetup`).
