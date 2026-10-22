# Third-party components

The SqlBackup source is MIT licensed. Bundled dependencies retain their own licenses; the project license does not replace them.

| Component | License / terms |
| --- | --- |
| Microsoft.Data.SqlClient and managed Microsoft/Azure dependencies | MIT; retain package copyright notices |
| Microsoft.Data.SqlClient.SNI.runtime | [Microsoft native SQL client terms](licenses/SqlClient-SNI.txt), not MIT |
| Microsoft.Data.Sqlite | MIT |
| Azure.Storage.Blobs and Azure.Storage.Common | MIT; retain Microsoft package notices |
| AWSSDK.S3 and AWSSDK.Core | [Apache 2.0](licenses/Apache-2.0.txt); retain Amazon package notices |
| SQLitePCLRaw | [Apache 2.0](licenses/Apache-2.0.txt), Copyright SourceGear, LLC |
| SQLite native engine | Public domain; retain notices supplied by its package |
| Self-contained .NET and Windows Desktop runtimes | Their supplied LICENSE and ThirdPartyNotices files, including notices for embedded components |
| Inno Setup installer engine | [Inno Setup license](licenses/Inno-Setup.txt); copyright Jordan Russell and Martijn Laan |

During packaging, Write-PackageNotices.ps1 reads restored package metadata and collects license/notice files into the installed Legal directory, with a versioned packages.json inventory. It includes transitively resolved packages; inclusion in the inventory is not a claim that every package asset is used at runtime. Runtime pack notices are included when self-contained publishing resolves those packs. New license expressions require explicit review.

The installer presents the SqlBackup MIT license and the separate native SQL client distribution terms. Keep those terms and upstream notices when redistributing binaries. Review updated dependencies before each release. See [the release checklist](docs/release-checklist.md).
