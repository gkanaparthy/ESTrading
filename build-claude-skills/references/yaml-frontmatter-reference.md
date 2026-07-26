# YAML Frontmatter Reference

Complete reference for SKILL.md frontmatter fields and requirements.

## Required Fields

### name
- **Type:** String (kebab-case only)
- **Example:** `build-claude-skills`
- **Rules:**
  - No spaces, capitals, or underscores
  - Should match folder name
  - Maximum 50 characters recommended
  - No "claude" or "anthropic" prefix (reserved)

### description
- **Type:** String (up to 1024 characters)
- **Structure:** [What it does] + [When to use it] + [Key triggers]
- **Required elements:**
  - Clear explanation of functionality
  - Trigger phrases users would actually say
  - File types if relevant
  - Specific use cases

**Example:**
```yaml
description: Analyzes Figma design files and generates developer handoff documentation. Use when user uploads .fig files, asks for "design specs", "component documentation", or "design-to-code handoff".
```

## Optional Fields

### license
- **Type:** String (SPDX identifier recommended)
- **Common values:** MIT, Apache-2.0, GPL-3.0
- **Use when:** Making skill open-source
- **Example:**
```yaml
license: MIT
```

### compatibility
- **Type:** String (1-500 characters)
- **Purpose:** Indicate environment requirements
- **Examples:**
```yaml
compatibility: Requires Python 3.8+, Network access for API calls

compatibility: |
  Required: NinjaTrader 8 compatible
  Framework: .NET Framework 4.8+
  Permission: File system write access
```

### metadata
- **Type:** YAML object
- **Purpose:** Custom key-value pairs for organization
- **Suggested fields:**
  - `author`: Creator name or company
  - `version`: Semantic version (e.g., 1.0.0)
  - `mcp-server`: Associated MCP server name
  - `category`: Type of skill
  - `tags`: Array of keywords
  - `documentation`: Link to detailed docs
  - `support`: Contact email for support

**Example:**
```yaml
metadata:
  author: Acme Corp
  version: 1.2.0
  mcp-server: acme-crm
  category: workflow-automation
  tags: [crm, sales, automation, pipeline]
  documentation: https://docs.example.com/skill-guide
  support: skill-support@example.com
```

## Complete Frontmatter Examples

### Minimal Skill
```yaml
---
name: simple-skill
description: Creates formatted reports. Use when user asks to "generate report" or "create documentation".
---
```

### Document Creation Skill
```yaml
---
name: pdf-document-creator
description: Creates professional PDF documents from templates. Use when user asks to "create a PDF", "generate a report", or "make a document". Supports invoices, proposals, and custom layouts.
license: MIT
metadata:
  author: Anthropic
  version: 1.0.0
  category: document-creation
  tags: [pdf, documents, templates, formatting]
---
```

### MCP-Enhanced Skill
```yaml
---
name: linear-sprint-planner
description: Manages Linear project sprints including planning, task creation, and team assignments. Use when user mentions "sprint planning", "create Linear tasks", "assign team members", or "plan sprint".
metadata:
  author: Project Management Team
  version: 2.1.0
  mcp-server: linear-api
  category: workflow-automation
  tags: [project-management, linear, sprint-planning, agile]
  documentation: https://docs.linear.com/skills
  support: support@linear.com
compatibility: Requires Linear MCP server v1.0+, API key with write permissions
---
```

### Domain-Specific Skill
```yaml
---
name: financial-compliance-checker
description: Validates financial transactions against compliance rules including sanctions lists and jurisdiction allowances. Use for transaction review, compliance checking, or regulatory validation workflows.
license: Apache-2.0
compatibility: |
  Required regulatory databases updated monthly
  Minimum API rate limit: 1000 req/hour
  Data: Processes sensitive financial data
metadata:
  author: Compliance Systems Inc
  version: 3.5.1
  category: compliance
  tags: [finance, compliance, regulation, aml, kyc]
  documentation: https://compliance.example.com/guide
  support: compliance-team@example.com
---
```

## Security Best Practices

### Allowed in Frontmatter
- Standard YAML types (strings, numbers, booleans, lists, objects)
- Custom metadata fields
- Long descriptions (up to 1024 characters)
- URLs in metadata
- Multi-line strings using pipe (|) or fold (>)

### Forbidden in Frontmatter
- XML angle brackets (< >) — security restriction
- Code execution in YAML (uses safe YAML parsing)
- Secrets or API keys (never store in frontmatter)
- Skills named with "claude" or "anthropic" (reserved)

### Why These Restrictions?
Frontmatter appears in Claude's system prompt. Any malicious content could potentially inject instructions or bypass safety measures. Safe YAML parsing prevents code execution.

## Testing Your Frontmatter

### Verify YAML Syntax
```bash
# Check if YAML is valid (requires yamllint)
yamllint SKILL.md

# Or manually validate structure:
# - Must start with ---
# - Must end with ---
# - All fields properly indented (2 spaces)
# - No unquoted special characters
```

### Test Description Triggering
1. Upload skill to Claude.ai
2. Ask: "When would you use the [skill-name] skill?"
3. Claude will quote your description back
4. Verify it includes needed trigger phrases
5. Iterate if needed

### Debug Frontmatter Errors
**Error:** "Invalid frontmatter"
- Check for missing `---` delimiters
- Verify YAML indentation (2 spaces, not tabs)
- Look for unclosed quotes
- Ensure no XML tags (< >)

**Error:** "Invalid skill name"
- Must be kebab-case
- No spaces, capitals, or underscores
- No "claude" or "anthropic" prefix

## Common Mistakes and Fixes

### Mistake 1: Missing YAML Delimiters
```yaml
# ❌ Wrong
name: my-skill
description: Does stuff

# ✅ Correct
---
name: my-skill
description: Does stuff
---
```

### Mistake 2: Inconsistent Indentation
```yaml
# ❌ Wrong
metadata:
author: Name
  version: 1.0.0

# ✅ Correct
metadata:
  author: Name
  version: 1.0.0
```

### Mistake 3: Generic Description
```yaml
# ❌ Wrong
description: Helps with projects.

# ✅ Correct
description: Manages Linear project sprints including planning and task creation. Use when user mentions "sprint planning", "create tasks", or "assign team members".
```

### Mistake 4: Description Too Long
```yaml
# ❌ Wrong (1500+ characters)
description: This skill does many things including ...very long description...

# ✅ Correct (under 1024 characters)
description: Creates formatted reports with templates. Use for invoices, proposals, contracts. Supports PDF export and email delivery.
```

### Mistake 5: Special Characters in Description
```yaml
# ❌ Wrong (XML tags)
description: Analyzes <ProjectHub> files and generates <output>

# ✅ Correct (plain text)
description: Analyzes ProjectHub files and generates formatted output
```
