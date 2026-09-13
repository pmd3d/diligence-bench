# Local installation and operation

The `diligence` CLI is a framework-dependent .NET 10 application. It runs locally, stores each research session on the local filesystem, and calls Amazon Bedrock, SEC, Exa, and public HTTP sources directly. It has no Python, `uv`, Modal, Docker, or server-deployment dependency.

## Requirements

- Linux or macOS with the .NET 10 SDK to build from this checkout. A machine receiving an already-published directory only needs the .NET 10 runtime.
- An AWS account/profile allowed to invoke the selected Bedrock model or inference profile in `us-east-1` (or an explicitly configured Region).
- A descriptive SEC user agent.
- An Exa API key when `web_search` is used.

The Bedrock Converse operation requires `bedrock:InvokeModel`. Restrict the permission's resources to the selected model or inference profile where the account setup permits it. The AWS SDK for .NET uses its standard credential chain, including the profile selected by `AWS_PROFILE`; credentials do not belong in the application config. See the official [AWS SDK credential resolution](https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/creds-assign.html) and [Bedrock Converse reference](https://docs.aws.amazon.com/cli/latest/reference/bedrock-runtime/converse.html).

## Build, publish, and install

From the repository root:

```bash
./scripts/build.sh
./scripts/publish.sh
./scripts/install.sh
```

`publish.sh` writes a Release build to `artifacts/diligence`. `install.sh` copies it to `~/.local/lib/diligence` and installs a launcher at `~/.local/bin/diligence`. If `~/.local/bin` is not on `PATH`, add it in your shell profile.

To publish elsewhere or install under another user-writable prefix:

```bash
./scripts/publish.sh /tmp/diligence-publish
./scripts/install.sh /home/you/tools /tmp/diligence-publish
```

The installation does not create, move, or delete research runs. Publishing and installation invoke only `dotnet` and ordinary filesystem utilities.

## Local configuration and credentials

Create a configuration outside the checkout, for example at `~/.config/diligence/config.json`:

```json
{
  "region": "us-east-1",
  "modelId": "your-verified-deepseek-model-or-inference-profile-id",
  "runsDirectory": "/home/you/diligence-runs",
  "secUserAgent": "Your Name your-email@example.com"
}
```

Use an absolute, user-writable `runsDirectory` on a volume that is backed up. The default is `~/diligence-runs`. Select the exact DeepSeek identifier only after verifying that it is accessible in the configured account and Region and supports the planned Converse workflow; the application does not silently substitute another model.

Configure credentials and secrets in the shell or an OS-backed secret injection mechanism:

```bash
export AWS_PROFILE=diligence
export EXA_API_KEY='your-exa-key'
export DILIGENCE_CONFIG="$HOME/.config/diligence/config.json"
```

`SEC_USER_AGENT` can be used instead of `secUserAgent` in JSON. Do not put AWS credentials or the Exa key in the checkout or application config. For identity setup, prefer short-lived or federated AWS credentials; the AWS documentation explains the supported profile sources.

## Run research

First verify the installed command and its typed tool surface:

```bash
diligence tools
```

Run a recorded response without network credentials:

```bash
diligence research \
  --ticker MSFT \
  --prompt-file /path/to/question.md \
  --replay-response /path/to/recorded-response.md
```

Run live research after verifying Bedrock access and the configured model:

```bash
diligence research --ticker MSFT --prompt-file /path/to/question.md
```

`--config`, `--runs-dir`, and `--region` override their corresponding defaults. Every attempt creates a new child directory beneath `runsDirectory`; application upgrades never overwrite these directories.

## Interrupt and restart safely

Press Ctrl+C once to request cancellation. The CLI marks the run incomplete, atomically updates its manifest, retains completed events and evidence, and generates `report.html`. It exits with status 130.

Start another attempt from the saved ticker and prompt with:

```bash
diligence restart --run <run-id>
```

`restart` always creates a new run ID. It does not resume a Bedrock conversation or alter the interrupted directory, so both attempts remain inspectable. The command accepts the same `--config`, `--runs-dir`, `--region`, and `--replay-response` overrides as `research`.

## Reopen, back up, and remove

Regenerate and print the path of a run's standalone HTML report:

```bash
diligence show --run <run-id>
```

Print only a completed memo with `diligence show --run <run-id> --answer`. Open the reported HTML path in a browser. The report uses relative links to saved evidence, so copy or archive the entire run directory rather than individual files.

Back up the complete configured `runsDirectory` with the local backup tool of your choice. Restore it at the same location or point `runsDirectory` at the restored location.

Upgrade by publishing again and rerunning `install.sh`. Remove only the installed application with:

```bash
./scripts/uninstall.sh
```

The uninstall script deliberately leaves the config, secrets, and research runs untouched.
