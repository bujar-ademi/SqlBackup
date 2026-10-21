# Contributing

Discuss larger changes in an issue before starting. Keep pull requests focused and explain the problem, resulting behavior and verification performed. Never include real SQL credentials, customer data, backup files or private server addresses in issues or test fixtures.

Use the SDK pinned in global.json on Windows. See [development](docs/development.md) for build and test commands. The tests are an executable, not a dotnet test project. Report skipped checks separately from passed checks.

Write readable C# with Allman braces for every conditional and loop, one statement per line, and one named type per matching file. Keep I/O asynchronous, preserve cancellation and return actionable errors. Keep SQL and storage credentials scoped to each job.

Changes to backup, transfer or deletion must preserve [backup and retention rules](docs/backup-and-retention.md). Use disposable test databases and folders. Do not run integration or installer tests against an existing installation or production system. Add focused regression checks for changed business behavior and update the user documentation.

Contributions are provided under the project's [MIT license](LICENSE). Dependency and bundled asset notices must remain intact.
