# diligence-bench

A rework in progress of an agentic financial diligence benchmark.

## Quickstart

## Build and test

```bash
dotnet restore Diligence.sln
dotnet build Diligence.sln --no-restore
dotnet test Diligence.sln --no-build
```

## Publish and install

```bash
./scripts/publish.sh
diligence tools
```

See [docs/local-installation.md](docs/local-installation.md) for AWS profile setup, secret configuration, durable storage, live and replayed runs, interruption/restart, reopening reports, backup, upgrades, and removal.

Rebuild or locate a run's standalone inspection report with:

```bash
dotnet run --project src/Diligence.Cli -- show --run <run-id> --runs-dir ./runs
```

Restart an interrupted or failed run as a new attempt while preserving the original artifacts:

```bash
dotnet run --project src/Diligence.Cli -- restart --run <run-id> --runs-dir ./runs --replay-response sample-response.md
```

Open the printed `report.html` path in a browser. The report keeps the original question, memo or partial output, source links and saved excerpts, expandable tool activity, errors, status, timing, and token usage together. To print only a completed memo, add `--answer`.

Prompts remain editable: revise `runs/<run-id>/prompt.md`, then pass it as `--prompt-file` to a new `research` command. Every attempt gets a fresh run directory, so the earlier report stays intact. On a budget limit, provider error, or Ctrl+C, the CLI records an incomplete or failed manifest and generates a report from the activity and evidence already saved.

`config.example.json` documents the local defaults. Set `DILIGENCE_CONFIG` to avoid repeating `--config`. `us-east-1` is the default Region. Before enabling live inference, choose a DeepSeek model ID or inference profile that your AWS account can access and confirm supports Bedrock Converse tool use. AWS credentials remain in the standard AWS credential chain, never in this configuration file.

## License

Released under [Apache-2.0](LICENSE).

© 2026 Paper Instruments, Inc. and Thoughtful Lab, Inc.
