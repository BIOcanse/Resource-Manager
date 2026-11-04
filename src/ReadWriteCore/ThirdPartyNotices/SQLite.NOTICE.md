# SQLite dependency notices

This SDK uses the following unchanged NuGet dependencies. Dependency licensing is
separate from the Resource Manager Apache-2.0 license.

## Microsoft.Data.Sqlite 10.0.14

Microsoft.Data.Sqlite and Microsoft.Data.Sqlite.Core 10.0.14 declare MIT.
Package copyright: © Microsoft Corporation. All rights reserved.
The exact upstream source LICENSE is included as Microsoft.Data.Sqlite.LICENSE.txt;
it retains Copyright (c) .NET Foundation and Contributors.
The Core package does not contain the native SQLite library.

Selected upstream source from the actual 10.0.14 package repository metadata:
https://github.com/dotnet/dotnet/tree/0427b9abbead3fa64c6655eb4290c537397131fa
License:
https://raw.githubusercontent.com/dotnet/dotnet/0427b9abbead3fa64c6655eb4290c537397131fa/LICENSE.TXT

## SQLitePCLRaw 3.0.5

SQLitePCLRaw.bundle_e_sqlite3 and SQLitePCLRaw.config.e_sqlite3:
Copyright 2014-2026 SourceGear, LLC.
SQLitePCLRaw.core and SQLitePCLRaw.provider.e_sqlite3:
Copyright 2014-2025 SourceGear, LLC.
The selected packages declare Apache-2.0. The complete original license is
included as SQLitePCLRaw.LICENSE.txt. No separate NOTICE, license exception or
EULA is contained in these selected NuGet packages. This notice retains their
actual package copyright declarations; it is not an upstream-authored NOTICE.

Core/provider repository commit from their actual package metadata:
https://github.com/ericsink/SQLitePCL.raw/tree/ed046114d5a30534e13294d94d78eb73de896ad4
Original license:
https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/ed046114d5a30534e13294d94d78eb73de896ad4/LICENSE.TXT
Config package repository commit: 96043b8cff323f21919df86a431c136655d81b4a.

## Native SQLite

The SQLite 3.53.4 package (Eric Sink; formerly SourceGear.sqlite3) supplies the selected native SQLite runtime through
the bundle dependency. Its original package LICENSE.txt is included unchanged
as SQLite.Native.LICENSE.txt and states SQLite is Public Domain:
https://sqlite.org/copyright.html
Package metadata also records Copyright 2014-2026 SourceGear, LLC.

These native public-domain statements do not replace the managed wrapper's
Apache-2.0 or the ADO.NET provider's MIT licenses. The consuming application's
selected target framework and RID determine which package runtime asset is
copied; the ReadWriteCore nupkg does not embed a second native SQLite binary.

We thank Microsoft, the .NET Foundation and Contributors, Eric Sink, SourceGear,
and the SQLite contributors. They have not endorsed this SDK.
