# Agent360 Domain Contracts (Phase 0-1)

Contracts are the formal boundaries of Agent360 as defined in `/mnt/d/VPS/.tmp/agent360-plan.md`.

## Core Entities
1. **Bot (`bot-definition.v1.json`)**: Đơn vị sản phẩm/tác nhân độc lập đầy đủ nhất, sở hữu mission, constitution, mandate, memory namespace, and capability inventory.
2. **Bot Deployment (`bot-deployment.v1.json`)**: Định nghĩa triển khai của một Bot trên environment/cluster cụ thể với kernel version và QwenPaw runtime reference.
3. **Bot Organization Binding (`bot-organization-binding.v1.json`)**: Gắn kết Bot vào Organization Cell và Identity Cell do IAM360 sở hữu.
4. **Tool Call Receipt (`tool-call-receipt.v1.json`)**: Bằng chứng kiểm toán bắt buộc cho mọi hành động ngoại biên của Bot thông qua Tool Gateway và IAM authorization.
5. **Legacy Mapping (`legacy-agent-id-mapping.v1.json`)**: Ánh xạ tương thích ngược từ các `agent_id` của QwenPaw/legacy sang `bot_id`.
