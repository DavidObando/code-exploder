# Demo bundles

`*.cxbundle.gz` files here are pre-baked analyses (docs/08 §M7) that the Gateway
seeds at startup (`Seed:Dir`, `Seed:Subject`, `Seed:Enabled`) so a fresh deploy
lands on a populated app. Regenerate with:

    dotnet run --project src/CodeExploder.Gateway -- --export-bundle <sessionId> seeds/<name>.cxbundle.gz
