# Agent360 Ecosystem Deployments

This directory contains container definitions and configuration manifests for the Agent360 multi-surface platform on target server **2nd server**:

## Services Overview

| Surface | Domain | Type | Tech Stack | Internal Port |
|---|---|---|---|---|
| **Frontend** | `https://agent360.antares.vn` | SPA & Marketplace | React 18 / Vite 6 / Tailwind CSS | 80 |
| **Backend API** | `https://api-agent360.antares.vn` | REST API | .NET 10 Modular Monolith | 8080 |
| **Community** | `https://community-agent360.antares.vn` | Discussion Forum | Discourse Stable + Solved + Gamification | 80 |

## Discourse Community Customization (Inspired by https://community.crewai.com)

The community forum is configured with:
- **Dark Horizon Theme**: Dark obsidian background `#07090E` with Agent360 brand orange accent `#FF7A00`.
- **Pre-seeded Categories**:
  1. Announcements (`#BF1E2E`)
  2. General Discussion (`#F1592A`)
  3. Agent Showcase (`#0088CC`)
  4. Multi-Agent Crews & ACP (`#7E3AF2`)
  5. Tools, MCP & Sandbox (`#3AB54A`)
  6. LLM Backends & Local Models (`#8C6238`)
  7. Troubleshooting & Support (`#E02424`)
  8. Job Board & Bounties (`#12A89D`)
- **Key Plugins**:
  - `discourse-solved`: Cho phép đánh dấu câu trả lời chính xác cho các câu hỏi kỹ thuật.
  - `discourse-reactions`: Hệ thống thả biểu cảm (reactions) cho code snippet và agent review.
  - `discourse-gamification`: Bảng xếp hạng và danh hiệu đóng góp (contributor badges).
  - `discourse-checklist`: Hỗ trợ danh sách kiểm tra tương tác cho các bước thiết lập Crew.

## Automated Execution

To deploy the entire stack to `/opt/agent360` on `2nd server`:

```bash
bash projects/agent360/scripts/deploy-agent360-stack.sh
```
