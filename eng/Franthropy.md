# Franthropy build dependency

Outside contributors can clone https://github.com/FranFkntastic/Franthropy,
check out the full commit after `0.1.0-commit.` in
`eng/Franthropy.Version.props`, and build with
`dotnet build -c Release -p:FranthropySource=Local -p:FranthropyRoot=<absolute-source-root>`.
Pass the same properties to tests. This source route requires no private feed.

The exact package pin is owned by `eng/Franthropy.Version.props`.
Populate the private local feed before restoring. Its default is
`$(LOCALAPPDATA)/Franthropy/packages`; override with
`-p:FranthropyFeed=<feed-directory>`. Packages are not published publicly.

Release builds use packages without a source checkout. Paired development
requires both `-p:FranthropySource=Local` and
`-p:FranthropyRoot=<absolute-source-root>` on dotnet build/test.
Missing roots and unknown modes fail explicitly. Rebuild in package mode
before release packaging.

Old `franthropy.ref` or `Franthropy.commit` files, where present, are retained
only for rollback and no longer select the build dependency.
