# Organization customization boundary

Organization-specific prompts, skills, MCP policy, Bot definitions and
runtime manifests belong in a derived project such as `agent360-gm`, not in
the Agent360 pattern repository and never in the clean QwenPaw checkout.

Do not place credentials, `/app/working*` data, channel state, or copied
third-party restricted skills here.
