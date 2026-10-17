#!/usr/bin/env python3
"""
Seed Discourse Categories, Custom Color Scheme, and Initial Pinned Topics
Modelled after the official https://community.crewai.com architecture.
"""

import json
import os
import sys

COMMUNITY_CATEGORIES = [
    {
        "name": "Announcements",
        "slug": "announcements",
        "color": "BF1E2E",
        "text_color": "FFFFFF",
        "description": "Important updates, release notes, and core architecture news from the Agent360 engineering team.",
        "permissions": {"everyone": 1} # read-only for public, admin write
    },
    {
        "name": "General Discussion",
        "slug": "general",
        "color": "F1592A",
        "text_color": "FFFFFF",
        "description": "General discussions about autonomous AI workers, enterprise multi-agent architectures, and agentic workflows."
    },
    {
        "name": "Agent Showcase",
        "slug": "showcase",
        "color": "0088CC",
        "text_color": "FFFFFF",
        "description": "Share your custom AI teammates, specialized roles, toolchains, and real-world task execution benchmarks."
    },
    {
        "name": "Multi-Agent Crews & ACP",
        "slug": "crews",
        "color": "7E3AF2",
        "text_color": "FFFFFF",
        "description": "Collaborate on hierarchical orchestrators, sequential pipelines, and agent communication protocol (ACP) patterns."
    },
    {
        "name": "Tools, MCP & Sandbox",
        "slug": "tools-mcp",
        "color": "3AB54A",
        "text_color": "FFFFFF",
        "description": "Model Context Protocol (MCP) integrations, Chromium DevTools automation, Linux kernel sandboxing, and security boundaries."
    },
    {
        "name": "LLM Backends & Local Models",
        "slug": "llms",
        "color": "8C6238",
        "text_color": "FFFFFF",
        "description": "Discussions on frontier models (Claude 3.5 Sonnet, GPT-4o, Grok-2) and quantized local inference (Qwen 2.5, Llama 3.1)."
    },
    {
        "name": "Troubleshooting & Support",
        "slug": "support",
        "color": "E02424",
        "text_color": "FFFFFF",
        "description": "Community assistance with container deployment, memory persistence, tool errors, and integration debugging."
    },
    {
        "name": "Job Board & Bounties",
        "slug": "jobs",
        "color": "12A89D",
        "text_color": "FFFFFF",
        "description": "Career opportunities, bounties, and contract openings for Agent Engineers and Autonomous Infrastructure Architects."
    }
]

TOP_TAGS = [
    "agent360", "multi-agent", "crewai-pattern", "tools", "mcp", "memory-reme",
    "sandbox", "hierarchical", "autonomous-swe", "vision-qa", "sre-sentinel",
    "qwenpaw", "postiz", "toonflow", "beszel", "infisical", "coolify"
]

COLOR_SCHEME_AGENT360_DARK = {
    "name": "Agent360 Horizon Dark",
    "base_scheme_id": "Dark",
    "colors": {
        "primary": "F0F2F5",          # Main text
        "secondary": "07090E",        # Main background (Jet Dark)
        "tertiary": "FF7A00",         # Brand Orange Accent
        "quaternary": "FF9838",       # Bright Orange
        "header_background": "0B0E16",# Top bar background
        "header_primary": "FFFFFF",   # Top bar icons and text
        "highlight": "FF7A00",        # Topic highlights
        "danger": "FF5555",           # Warning/error
        "success": "10B981",          # Verified green
        "love": "F43F5E"              # Like heart color
    }
}

def generate_discourse_seed_manifest():
    manifest = {
        "title": "Agent360 Official Community Forum",
        "domain": "community-agent360.antares.vn",
        "theme": "Agent360 Dark Theme",
        "color_scheme": COLOR_SCHEME_AGENT360_DARK,
        "categories": COMMUNITY_CATEGORIES,
        "tags": TOP_TAGS,
        "pinned_topics": [
            {
                "category": "announcements",
                "title": "Welcome to Agent360 Community — The Enterprise AI Teammate Platform 🚀",
                "raw": """Welcome to the official **Agent360 Community Forum**!

Agent360 is an enterprise multi-agent control plane inspired by top autonomous engineering patterns (like x.ai/bot and CrewAI).

### Key Rules & Philosophy:
1. **No Fake Done**: When sharing agent benchmarks, always attach authoritative logs, test passes, or verifiable artifacts.
2. **Specialized Teammates**: Don't force one prompt to do everything. Build dedicated SWE, QA, SRE, and Research bots.
3. **Open Collaboration**: Share your MCP tools, prompt contracts, and orchestration DAGs!
"""
            },
            {
                "category": "crews",
                "title": "RFC: Standardizing Agent Communication Protocol (ACP) for Hierarchical Delegation",
                "raw": """Let's discuss best practices for coordinating hierarchical multi-agent crews with bounded budgets, fail-closed approval gates, and authoritative readbacks.

How do you handle runaway token spend when agents delegate subtasks? Share your patterns below!"""
            }
        ]
    }
    
    out_path = "projects/agent360/deploy/community-seed.json"
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
    print(f"Generated Discourse Community seed manifest: {out_path}")

if __name__ == "__main__":
    generate_discourse_seed_manifest()
