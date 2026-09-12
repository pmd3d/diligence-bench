# Personal stock research: F# / .NET 10 plan

Status: proposed; planning only. This replaces the earlier full-benchmark rewrite plan and incorporates the final scope: Bedrock, EC2, custom stock prompts, and manual review. No automated judge or Codex integration is required.

## Intended outcome

Build a small F# application targeting `net10.0` that takes a stock and your own question, calls a Bedrock model with this project's research tools, and saves a memo with inspectable evidence and tool activity.

```text
Your stock + prompt
        |
F# application on EC2
        |
Bedrock model <--> F# SEC and web tools
        |
Memo + sources + tool history
        |
Your manual review
```

Working assumption: Bedrock is the research model provider. Start with a CLI operated on EC2; hosting alone does not require a public website. A browser interface can be added later if desired.

## Scope

| Existing component | Treatment |
| --- | --- |
| Finance prompt and finance agent rules | Reuse as editable assets, adapting benchmark-specific instructions to a user-supplied question. |
| SEC EDGAR tools | Port company resolution, filing search/content, financial facts, extraction, caching, and truncation. |
| Web search/fetch | Port Exa search and HTTP fetch, including saved evidence and descriptive tool schemas. |
| Final-answer tool | Adapt to write the memo to the individual run's `answer.md`. |
| Agent loop | Implement in F# around Bedrock tool use, with configurable execution budgets. |
| Results and HTML reporting | Adapt the useful presentation ideas to individual research sessions and manual inspection. |
| Dataset loading, Harbor, model sweeps, ablation CSV | Outside this application's scope. |
| Rubric grading and vendor CLI agents | Outside scope; review is manual. |
| Modal and Python Agents SDK | Not needed for the new typed research-tool workflow. |
| Arbitrary shell and apply-patch tools | Defer; the first version exposes SEC, web, and memo-writing operations. |

This is a focused rewrite using the existing research assets, rather than a drop-in benchmark replacement. Preserve the original Python benchmark separately during development. Do not alter its public CSV schema.

The repository's Modal-only rule governs its `SandboxAgent` agents. The new application does not instantiate those agents or execute arbitrary model-generated code. It invokes typed research functions on EC2. If shell tools become necessary later, design isolation separately; do not grant model-generated shell commands access to the application host or its credentials.

## User workflow

Proposed commands, not yet implemented:

```text
diligence research --ticker <symbol> --prompt-file question.md --config config.json
diligence show --run <run-id>
diligence tools
```

1. Supply a ticker/company and a question. Optionally include an investment thesis, exchange/CIK, and as-of date. No brokerage connection or portfolio import is necessary.
2. Resolve the company and select an editable research prompt and tool set.
3. Call the configured Bedrock model; display progress as it requests SEC and web tools.
4. Save the memo, sources, configuration, and tool history.
5. Read the report and manually assess it. Edit the prompt and start a new run when you want another attempt.

Use a fresh run directory per experiment. A prompt can be inline or loaded from a file. Never silently substitute a company when the ticker is ambiguous. Show missing SEC coverage or unavailable sources clearly.

An example question template could ask: “Review the latest filings for <company>. What evidence supports or weakens my thesis, what changed in cash flow and debt, and what questions should I investigate next?” This is an input template, not an investment conclusion.

## F# design

Start with three projects and one test project:

```text
src/Diligence.Core/            # requests, evidence, prompts, tools, budgets
src/Diligence.Infrastructure/  # Bedrock, SEC, Exa, HTTP fetch, file storage
src/Diligence.Cli/             # commands, research loop, reporting
tests/Diligence.Tests/
assets/prompts/
deploy/
```

Use immutable records and discriminated unions for `ResearchRequest`, `EvidenceItem`, `ToolCall`, `RunStatus`, and errors. Use `task {}` plus `CancellationToken` for I/O, explicit JSON DTOs with `System.Text.Json`, and structured logging. Keep prompt and tool-set differences in configuration.

Use `AWSSDK.BedrockRuntime` from AWS SDK for .NET V4 and `HttpClient` for SEC/Exa. Select and pin remaining CLI, HTML parsing, and testing dependencies during the foundation stage. Pin the .NET SDK and NuGet versions. SDK `10.0.112` is already installed on the development machine.

Call F# research functions directly from the Bedrock tool dispatcher. MCP transport is optional later if another client needs to consume these tools; it is not required for this standalone workflow.

## Bedrock integration

Use the Converse API with a configured model/inference profile that supports tool use in the chosen Region. Model availability and capabilities must be checked for that deployment. [AWS Converse API](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_runtime_Converse.html), [model compatibility](https://docs.aws.amazon.com/bedrock/latest/userguide/models.html)

The loop supplies the system prompt, user request, and tool definitions; validates each requested tool and its arguments; executes it; and sends the result back with the matching tool-use ID. Stop on a submitted memo, explicit completion, cancellation, or budget exhaustion. Save partial progress if a run cannot finish.

Use EC2 instance-role credentials through the AWS SDK credential chain. Local development can use an AWS profile. Scope permissions to the selected model/profile and any supporting resources required by that profile. [AWS SDK credential resolution](https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/creds-assign.html)

Make model ID/profile, Region, sampling settings, maximum turns, output-token limits, tool-call limits, fetched-byte limits, and elapsed-time limits configurable. Record effective settings for each run. Use bounded retries for transient failures; do not blindly repeat completed tool actions.

Do not port the existing OpenRouter sampling translation or Bedrock provider preference inside OpenRouter: this application calls Bedrock directly. Confirm model-specific request fields in the Bedrock adapter.

## Evidence and manual review

```text
runs/<run-id>/
  request.json
  prompt.md
  manifest.json
  events.jsonl
  sources.json
  evidence/
  answer.md
  report.html
```

The manifest records the Bedrock model/profile, Region, prompt/tool versions, application version, effective configuration, and start/end times. Sources retain URLs, retrieval dates, filing/reporting periods where available, and content hashes.

The report should make manual inspection easy:

- Original question and final memo.
- Clickable source references and corresponding saved excerpts.
- Expandable tool calls, arguments, outputs, and errors.
- Missing evidence and incomplete-run status.
- Elapsed time and token usage where available.

Preserve the distinction between source content, model conclusions, and your supplied thesis. Render fetched/model-generated text safely in HTML. Provide a local HTML report first; retrieving it from EC2 should not require a public web endpoint.

Write final artifacts atomically. Keep useful partial results on timeout or interruption. Do not produce a score or pass/fail judgment. A later comparison view can show two manually selected runs, without running a benchmark or automatic model sweep.

## EC2 deployment

Deploy one Linux EC2 instance with a published .NET application and encrypted EBS storage for research runs. The model is hosted by Bedrock, so the proposed application needs no GPU. Choose instance size after measuring document processing; there is no reason to select an expensive instance during planning.

Use a dedicated non-root user and reproducible provisioning/configuration. Start with operator-invoked CLI runs through restricted SSH or Systems Manager access. If long-running jobs need management, add a systemd worker rather than a public service by default.

Store the Exa key outside the checkout with restrictive permissions, and configure SEC's identifying user-agent. Use the instance role for AWS access. Allow outbound access to Bedrock, SEC, Exa, and public research sources. Keep credentials and private holding context out of logs.

Because the app fetches URLs on an EC2 host, block metadata, private, and link-local addresses, including redirects and DNS resolution changes. Bound response sizes and confine saved files to the run directory. These controls protect instance-role credentials from model-requested URLs.

The deployment deliverable includes provisioning, installation/update commands, IAM configuration, persistent-directory layout, interrupted-run behavior, backup/restore, and teardown instructions. Optional S3 backup can follow. No AWS resources are created by this planning work.

Before deployment, choose AWS account/Region, an accessible tool-capable Bedrock model/profile, the Linux image, network access, and instance size. Before the first real research run, provide a stock and prompt. These choices do not block building the local application and recorded-response tests.

## Delivery sequence

| Stage | Work | Acceptance |
| --- | --- | --- |
| 1. Foundation | F# solution, configuration, prompt loading, run storage, Bedrock adapter | One custom question produces saved output using a replayed response; a separate credentialed smoke call verifies Bedrock access. |
| 2. Research tools | Port SEC/web functions and schemas, evidence saving, Bedrock tool loop, final memo | A question uses the tools and produces a memo whose references resolve to saved evidence. |
| 3. Manual inspection | HTML report, progress display, error/partial-run handling, editable prompts | You can inspect the question, answer, sources, and each tool call, then try a revised prompt. |
| 4. EC2 operation | Provisioning, instance role, secret configuration, persistent storage, operating guide | Run one question on EC2, interrupt/restart safely, and reopen saved results. |
| 5. Refinement | Improve prompts/tools based on your feedback; optional two-run comparison | You can compare chosen experiments without a judge or full-benchmark workflow. |

Keep tests focused on realistic failure modes: Bedrock tool-use IDs, malformed arguments, SEC extraction fixtures, evidence references, fetch restrictions, budgets/cancellation, artifact integrity, and HTML escaping. Use recorded provider responses for normal tests. Live tests should be small and explicit.

## Completion criteria

You can run one prompt about a stock you own on EC2, using a selected Bedrock model and the ported research tools; inspect the memo, evidence, and tool history manually; change the prompt; and try again. All application-owned runtime code is F# on .NET 10. The workflow requires no benchmark dataset, Harbor environment, Modal account, Codex installation, or automated judge.

Planning validation: inspected repository source/tests, checked the local .NET SDK, and consulted official AWS documentation. No migration code, deployments, model calls, or test runs were performed.
