# QwenPaw adapter boundary

QwenPaw is an implementation/runtime adapter. This directory defines the
mapping contract only; it intentionally contains no QwenPaw source.

```text
Agent360 Bot
  -> Bot Deployment
  -> QwenPaw runtime_id
  -> machine identity
  -> IAM360 Identity Cell
```

The adapter must never:

- use `runtime_id`, a container name, or a channel username as `bot_id`;
- create an IAM Identity Cell;
- bypass IAM360 policy decisions;
- share a memory namespace or machine identity between unrelated Bots;
- import QwenPaw runtime volumes into Agent360 source.

The exact QwenPaw tag/commit and image digest belong in the derived project's
lock/deployment record, not in this reusable contract.
