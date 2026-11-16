# Branding

The Masterly.NightShift mark is a cream crescent moon inside an orbit ring, with an ember dot riding the orbit: the night, work that comes round on a schedule, and a job in flight.

| Color | Hex | Used for |
|---|---|---|
| Abyss | `#0A2530` | Darkest tile stop, dark backgrounds |
| Deep teal | `#145A6B` | Lightest tile stop, primary brand color on light backgrounds |
| Orbit mint | `#5FE0C8` | The orbit, accents and links on dark backgrounds |
| Moonlight | `#F6E9CF` | The moon, light text on dark backgrounds |
| Ember | `#FF7A45` | The job dot; one warm accent, used sparingly |

`assets/logo.svg` is the source of the mark. The files in this folder are the sources of the PNGs, which are rendered from them:

| Source | Output | Size |
|---|---|---|
| `logo.html` | `repo_image.png` (the NuGet package icon, at the repository root) | 512 x 512, transparent corners |
| `readme-banner.html` | `assets/readme-banner.png` (top of the README) | 1200 x 300, transparent corners |
| `social-card.html` | `website/static/img/social-card.png` (link previews of the docs site) | 1200 x 630 |

The docs site also uses the mark as `website/static/img/logo.svg` and `favicon.svg`; copy `assets/logo.svg` there after a change.

## Rendering

With Microsoft Edge or Google Chrome in headless mode, from the repository root (PowerShell):

```text
$browser = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
$root = (Get-Location).Path -replace '\\', '/'
& $browser --headless=new --hide-scrollbars --default-background-color=00000000 --window-size=512,512 --screenshot="$PWD\repo_image.png" "file:///$root/assets/branding/logo.html"
& $browser --headless=new --hide-scrollbars --default-background-color=00000000 --virtual-time-budget=3000 --window-size=1200,300 --screenshot="$PWD\assets\readme-banner.png" "file:///$root/assets/branding/readme-banner.html"
& $browser --headless=new --hide-scrollbars --virtual-time-budget=3000 --window-size=1200,630 --screenshot="$PWD\website\static\img\social-card.png" "file:///$root/assets/branding/social-card.html"
```

`--virtual-time-budget` gives the web fonts time to load.
