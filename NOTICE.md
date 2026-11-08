# Notice

## Origin of the source code

Masterly.NightShift started from the source of Hangfire 1.8.25, an open-source background job library by Hangfire OÜ
(<https://github.com/HangfireIO/Hangfire>). Substantial parts of this repository are modified versions of that code.
Masterly.NightShift then added its own features on top (see `docs/whats-new-next.md`).

## License of the derived parts

The Hangfire code is licensed under the GNU Lesser General Public License, version 3 or (at your option) any later
version. That license applies to every file that carries the header

```
This file is part of Hangfire. Copyright © … Hangfire OÜ.
```

Those headers must stay in the files. They are not an oversight, and removing them would break the license terms. For
those files, and for the library as a whole when it is distributed, the LGPL v3 applies in addition to `LICENSE.txt`:

- you may use the library from software under any license, including closed-source software;
- if you modify the LGPL-covered files and distribute the result, you must make those modifications available under the
  LGPL v3;
- a copy of the license has to accompany any distribution. The full text of the LGPL v3 is in `LICENSE-LGPL-3.0.txt`,
  and of the GPL v3 it builds on in `LICENSE-GPL-3.0.txt` (also at <https://www.gnu.org/licenses/lgpl-3.0.txt> and
  <https://www.gnu.org/licenses/gpl-3.0.txt>).

Code and assets written for Masterly.NightShift (the files without that header, such as the branding in `assets/` and
the documentation in `docs/`) are covered by the MIT license in `LICENSE.txt`.

## NuGet packages

The assemblies in every package contain both kinds of code, so every package declares the license expression
`MIT AND LGPL-3.0-or-later` and carries `LICENSE.txt`, `LICENSE-LGPL-3.0.txt`, `LICENSE-GPL-3.0.txt`, this notice and
`THIRD-PARTY-NOTICES.md`. If you redistribute the packages or their assemblies, keep those files with them.

## Independence

Masterly.NightShift is an independent project. It is not affiliated with, endorsed by or supported by Hangfire OÜ,
and it does not use the Hangfire name, logo or website. Please report problems with it here, not to the Hangfire project.

## Third-party components

See `THIRD-PARTY-NOTICES.md`.
