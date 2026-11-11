# Phase 3 — Interface (WPF)

> Objectif : rendre le moteur utilisable sans jamais embellir la vérité.
> Priorité rappelée par le cahier des charges : la qualité du filtrage réel et la
> fiabilité du moteur passent avant les effets visuels.

## Ce qui a été construit

`src/UI` — application WPF (`Nightshift.exe`), .NET 8, aucune dépendance tierce.

| Élément | Choix |
|---|---|
| Fenêtre | 424 px de large, hauteur ajustée au contenu (`SizeToContent="Height"`), sans cadre système |
| Chrome | `WindowChrome` + barre de titre dessinée, `DWMWA_USE_IMMERSIVE_DARK_MODE`, coins arrondis DWM |
| État | interrupteur animé (160 ms), libellé « Écran non modifié » / « Filtre appliqué » |
| Température | consigne 1 900 → 6 500 K, lecture géante, nom d'ambiance (Nuit / Soir / Équilibré / Jour) |
| Presets | Jour 6500 · Équilibré 5000 · Soir 3800 · Nuit 2700, transition de 550 ms |
| Luminosité | 5 → 100 %, backend matériel détecté au démarrage, sinon atténuation gamma annoncée comme telle |
| Vérité | ligne « Effectif à l'écran : … K — intensité limitée à … % » |
| Diagnostic | bandeau HDR, erreur moteur en rouge, nombre d'écrans et mode SDR/HDR |

Le démarrage est **passif** : l'écran n'est pas touché tant que l'interrupteur n'est
pas actionné. Aucun réglage n'est persisté pour l'instant (profils = Phase 4).

## La température effective, et pourquoi elle est affichée

Windows refuse les tables gamma trop éloignées de la sienne. Quand il refuse, le
moteur applique un mélange entre la table du pilote et la cible : la consigne n'est
plus ce que l'écran montre.

- `src/Core/Color/EffectiveTemperature.cs` estime la température réellement
  présente en partant de la table **écrite**, jamais de la consigne : rapport
  appliqué/origine au code maximal de chaque canal, retour en lumière linéaire,
  puis recherche du point blanc le plus proche dans le lieu de Planck.
- `DisplayFilterEngine.EffectiveKelvin` et `EffectiveStrengthMin` exposent le
  résultat (entre plusieurs écrans, la valeur la plus défavorable est retenue).

Conséquence visible : à 2 700 K sur la dalle de référence, l'interface affiche
**3 498 K — intensité limitée à 70 %**, avec l'explication du plafond. C'est moins
flatteur qu'un « 2 700 K », et c'est exact.

### Correction apportée au moteur

L'intensité mémorisée par écran n'était réinitialisée qu'au-dessus de 3 500 K :
revenir de 2 700 K à 3 800 K conservait 70 % d'intensité, donc un écran plus chaud
que demandé. Une cible plus froide s'écartant toujours moins de la table neutre,
`SetTarget` efface désormais l'intensité mémorisée dès que la consigne remonte ;
si la pleine intensité est encore refusée, `ApplyCompliant` redescend aussitôt,
sans image intermédiaire visible.

Mesuré après correction : 3 800 K appliqué à **100 %**, effectif **3 800 K**.

## Vérification réelle

`Nightshift.exe --smoke` charge le vrai XAML, ouvre la fenêtre, pilote l'interface
et vérifie trois choses que la compilation ne prouve pas : aucune erreur de liaison
de données (écoute de `PresentationTraceSources`), le moteur reçoit bien les ordres,
et l'écran est restauré. Journal : `%LOCALAPPDATA%\Nightshift\ui-smoke.log`.

Résultat du 11 novembre 2026 :

```
fenêtre      : 424×539 px
activé       : consigne 3400 K · moteur 3400 K · effectif 3400 K · intensité 100 % · écriture ok=True
2700 K       : moteur 2700 K · effectif 3498 K · intensité 70 %
3800 K       : moteur 3800 K · effectif 3800 K
luminosité   : consigne 40 % · matériel=True · niveau lu 40
désactivé    : moteur 6500 K · restauration exacte=True
luminosité   : restaurée à 70 % · niveau lu 70
liaisons     : 0 erreur

UI SMOKE: PASS
```

Aucun fichier `restore.pending` ne subsiste après la fermeture : le drapeau de
restauration est bien levé puis retiré. Tests unitaires : **41/41** (dont 8 sur
l'estimation de température effective). Selftest moteur : **PASS**, résultats
inchangés (2 700 K à 70 %, 2 000 K à 50 %, restauration exacte).

## Limites assumées

- **Pas d'icône de notification** : fermer la fenêtre quitte l'application et
  restaure l'écran. Le gardien externe et le tray arrivent avec la Phase 4.
- **Aucune persistance** : la température et la luminosité repartent de leurs
  valeurs par défaut au lancement (profils en Phase 4).
- **HDR** : le bandeau existe, mais aucun écran HDR n'est disponible ici pour le
  voir apparaître en conditions réelles.
- **Une seule dalle testée** : le code est par écran, la machine n'a qu'un écran.
  Changement de topologie géré par `SystemEvents.DisplaySettingsChanged`.
- **Esthétique non vérifiée visuellement** : la mise en page est validée par la
  compilation XAML, le chargement réel et l'absence d'erreur de liaison, pas par
  une revue d'image.

## Lancer

```powershell
# interface
C:\Users\Galaad\Desktop\NIGHTSHIFT\src\UI\bin\Release\net8.0-windows\Nightshift.exe

# vérification de l'interface
Nightshift.exe --smoke   # code de sortie 0 = tout est passé
```
