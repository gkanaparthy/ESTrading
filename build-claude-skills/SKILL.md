---
name: build-claude-skills
description: Complete guide for creating Claude Skills with best practices. Use when building a new skill, designing skill structure, writing skill documentation, or need troubleshooting for skill issues. Covers planning, technical requirements, YAML frontmatter, testing, and distribution.
license: MIT
metadata:
  author: Anthropic
  version: 1.0.0
  category: development
  tags: [skills, claude, development, best-practices]
---

# Build Claude Skills

A comprehensive guide to creating high-quality Claude Skills that follow Anthropic's best practices and design principles.

## What is a Skill?

A skill is a folder containing:
- **SKILL.md** (required): Instructions in Markdown with YAML frontmatter
- **scripts/** (optional): Executable code (Python, Bash, etc.)
- **references/** (optional): Documentation loaded as needed
- **assets/** (optional): Templates, fonts, icons used in output

Skills are one of the most powerful ways to customize Claude for specific workflows. Instead of re-explaining preferences every conversation, skills let you teach Claude once and benefit every time.

## Core Design Principles

### Progressive Disclosure
Skills use a three-level system to minimize token usage:

1. **First level (YAML frontmatter)**: Always loaded in Claude's system prompt. Provides just enough info for Claude to know when each skill should be used.
2. **Second level (SKILL.md body)**: Loaded when Claude thinks the skill is relevant. Contains full instructions and guidance.
3. **Third level (Linked files)**: Additional files in the skill directory that Claude discovers only as needed.

### Composability
Claude can load multiple skills simultaneously. Your skill should work well alongside others.

### Portability
Skills work identically across Claude.ai, Claude Code, and API without modification (if dependencies are supported).

---

## Step 1: Planning and Use Cases

Before writing code, identify 2-3 concrete use cases your skill should enable.

### Good Use Case Definition

```
Use Case: Project Sprint Planning
Trigger: User says "help me plan this sprint" or "create sprint tasks"
Steps:
  1. Fetch current project status from Linear (via MCP)
  2. Analyze team velocity and capacity
  3. Suggest task prioritization
  4. Create tasks in Linear with proper labels and estimates
Result: Fully planned sprint with tasks created
```

### Common Skill Categories

**Category 1: Document & Asset Creation**
- Used for: Creating consistent, high-quality output (documents, presentations, designs, code)
- Real example: frontend-design skill
- Key techniques: Embedded style guides, templates, quality checklists

**Category 2: Workflow Automation**
- Used for: Multi-step processes with consistent methodology
- Real example: skill-creator skill
- Key techniques: Step-by-step validation gates, templates, iterative refinement

**Category 3: MCP Enhancement**
- Used for: Workflow guidance to enhance MCP server tool access
- Real example: sentry-code-review skill
- Key techniques: Multi-step MCP coordination, domain expertise, error handling

### Define Success Criteria

Quantitative metrics:
- Skill triggers on 90% of relevant queries
- Completes workflow in X tool calls
- 0 failed API calls per workflow

Qualitative metrics:
- Users don't need to prompt Claude about next steps
- Workflows complete without user correction
- Consistent results across sessions

---

## Step 2: Technical Requirements

### File Structure
```
your-skill-name/
├── SKILL.md                 # Required - main skill file
├── scripts/                 # Optional - executable code
│   ├── process_data.py
│   └── validate.sh
├── references/              # Optional - documentation
│   ├── api-guide.md
│   └── examples/
└── assets/                  # Optional - templates, etc.
    └── report-template.md
```

### Critical Rules

**SKILL.md naming:**
- Must be exactly `SKILL.md` (case-sensitive)
- No variations accepted (SKILL.MD, skill.md, etc.)

**Skill folder naming:**
- Use kebab-case: `notion-project-setup` ✅
- No spaces: `Notion Project Setup` ❌
- No underscores: `notion_project_setup` ❌
- No capitals: `NotionProjectSetup` ❌

**No README.md inside skill folder:**
- All documentation goes in SKILL.md or references/
- When distributing via GitHub, keep a repo-level README for humans

---

## Step 3: YAML Frontmatter (Critical!)

The YAML frontmatter is how Claude decides whether to load your skill.

### Minimal Required Format
```yaml
---
name: your-skill-name
description: What it does. Use when user asks to [specific phrases].
---
```

### Field Requirements

**name (required):**
- kebab-case only
- No spaces or capitals
- Should match folder name

**description (required):**
- MUST include BOTH:
  - What the skill does
  - When to use it (trigger conditions)
- Under 1024 characters
- No XML tags (< or >)
- Include specific tasks users might say
- Mention file types if relevant

### Examples of Good Descriptions

```yaml
# Good - specific and actionable
description: Analyzes Figma design files and generates developer handoff documentation. Use when user uploads .fig files, asks for "design specs", "component documentation", or "design-to-code handoff".

# Good - includes trigger phrases
description: Manages Linear project workflows including sprint planning, task creation, and status tracking. Use when user mentions "sprint", "Linear tasks", "project planning", or asks to "create tickets".

# Good - clear value proposition
description: End-to-end customer onboarding workflow. Handles account creation, payment setup, and subscription management. Use when user says "onboard new customer", "set up subscription", or "create account".
```

### Examples of Bad Descriptions

```yaml
# Too vague
description: Helps with projects.

# Missing triggers
description: Creates sophisticated multi-page documentation systems.

# Too technical, no user triggers
description: Implements the Project entity model with hierarchical relationships.
```

### Optional Fields

```yaml
license: MIT                    # For open-source skills

compatibility: |               # Environment requirements
  Requires: Python 3.8+
  Network: API access needed

metadata:                       # Custom key-value pairs
  author: Your Name
  version: 1.0.0
  mcp-server: service-name
```

### Security Restrictions

Forbidden in frontmatter:
- XML angle brackets (< >)
- Skills with "claude" or "anthropic" in name (reserved)

Why? Frontmatter appears in Claude's system prompt. Malicious content could inject instructions.

---

## Step 4: Writing the Main Instructions

After the frontmatter, write clear, actionable instructions in Markdown.

### Recommended Structure

```markdown
# Your Skill Name

## Instructions

### Step 1: [First Major Step]
Clear explanation of what happens.

Example:
\`\`\`bash
python scripts/fetch_data.py --project-id PROJECT_ID
\`\`\`

Expected output: [describe what success looks like]

### Step 2: [Next Step]
...

## Examples

### Example 1: [common scenario]
User says: "Set up a new marketing campaign"

Actions:
1. Fetch existing campaigns via MCP
2. Create new campaign with provided parameters

Result: Campaign created with confirmation link

## Troubleshooting

### Error: [Common error message]
Cause: [Why it happens]
Solution: [How to fix]
```

### Best Practices for Instructions

**Be Specific and Actionable:**
```
✅ Good:
Run `python scripts/validate.py --input {filename}` to check data format.
If validation fails, common issues include:
- Missing required fields (add them to the CSV)
- Invalid date formats (use YYYY-MM-DD)

❌ Bad:
Validate the data before proceeding.
```

**Include Error Handling:**
```markdown
## Common Issues

### MCP Connection Failed
If you see "Connection refused":
1. Verify MCP server is running: Check Settings > Extensions
2. Confirm API key is valid
3. Try reconnecting: Settings > Extensions > [Service] > Reconnect
```

**Reference Bundled Resources Clearly:**
```markdown
Before writing queries, consult `references/api-patterns.md` for:
- Rate limiting guidance
- Pagination patterns
- Error codes and handling
```

**Use Progressive Disclosure:**
- Keep SKILL.md focused on core instructions
- Move detailed documentation to `references/` and link to it
- This minimizes token usage while maintaining expertise

---

## Step 5: Testing Your Skill

### Testing Levels

**Manual testing (Claude.ai):** Run queries directly, observe behavior. Fast iteration, no setup required.

**Scripted testing (Claude Code):** Automate test cases for repeatable validation.

**Programmatic testing (Skills API):** Build evaluation suites for systematic testing.

### Recommended Test Coverage

**1. Triggering Tests**
Goal: Ensure your skill loads at the right times.

Should trigger:
- "Help me set up a new [service] workspace"
- "I need to create a [thing] in [service]"
- Paraphrased requests matching your use case

Should NOT trigger:
- Unrelated topics
- Other service names
- Generic requests outside your scope

**2. Functional Tests**
Goal: Verify correct outputs.

Test cases:
- Valid outputs generated
- API calls succeed
- Error handling works
- Edge cases covered

**3. Performance Comparison**
Goal: Prove the skill improves results.

Compare with/without skill:
- Number of back-and-forth messages
- Failed API calls
- Tokens consumed
- Time to completion

### Pro Tip: Iterate on Single Tasks First

The most effective skill creators iterate on a single challenging task until Claude succeeds, then extract the approach into a skill. This leverages Claude's in-context learning and provides faster feedback.

---

## Step 6: Iteration Based on Feedback

Skills are living documents. Plan to iterate.

**Undertriggering signals:**
- Skill doesn't load when it should
- Users manually enabling it
- Support questions about when to use it

Solution: Add more detail and nuance to description, including keywords.

**Overtriggering signals:**
- Skill loads for irrelevant queries
- Users disabling it
- Confusion about purpose

Solution: Add negative triggers, be more specific.

**Execution issues:**
- Inconsistent results
- API call failures
- User corrections needed

Solution: Improve instructions, add error handling.

---

## Step 7: Distribution and Sharing

### Current Distribution Model (January 2026)

**Individual users:**
1. Download the skill folder
2. Zip the folder
3. Upload to Claude.ai via Settings > Capabilities > Skills
4. Or place in Claude Code skills directory

**Organization-level skills:**
- Admins can deploy workspace-wide with automatic updates

### Recommended Approach

1. **Host on GitHub**
   - Public repo for open-source skills
   - Clear README with installation instructions
   - Example usage and screenshots

2. **Document in Your Project Repo**
   - Link to skills from documentation
   - Explain the value of using both together
   - Provide quick-start guide

3. **Create Installation Guide**
```markdown
# Installing the [Your Service] Skill

1. Download the skill:
   - Clone repo: `git clone https://github.com/yourcompany/skills`
   - Or download ZIP from Releases

2. Install in Claude:
   - Open Claude.ai > Settings > skills
   - Click "Upload skill"
   - Select the skill folder (zipped)

3. Enable the skill:
   - Toggle on the [Your Service] skill
   - Ensure your MCP server is connected

4. Test:
   - Ask Claude: "Set up a new project in [Your Service]"
```

### Positioning Your Skill

Focus on outcomes, not features:

```
✅ Good:
"The ProjectHub skill enables teams to set up complete project
workspaces in seconds — including pages, databases, and
templates — instead of spending 30 minutes on manual setup."

❌ Bad:
"The ProjectHub skill is a folder containing YAML frontmatter
and Markdown instructions that calls our MCP server tools."
```

---

## Common Patterns

### Pattern 1: Sequential Workflow Orchestration
Use for: Multi-step processes in specific order.

Key techniques:
- Explicit step ordering
- Dependencies between steps
- Validation at each stage
- Rollback instructions for failures

### Pattern 2: Multi-MCP Coordination
Use for: Workflows spanning multiple services.

Key techniques:
- Clear phase separation
- Data passing between MCPs
- Validation before moving to next phase
- Centralized error handling

### Pattern 3: Iterative Refinement
Use for: Output quality improves with iteration (e.g., report generation).

Key techniques:
- Explicit quality criteria
- Iterative improvement
- Validation scripts
- Know when to stop iterating

### Pattern 4: Context-Aware Tool Selection
Use for: Same outcome, different tools based on context.

Key techniques:
- Clear decision criteria
- Fallback options
- Transparency about choices

### Pattern 5: Domain-Specific Intelligence
Use for: Specialized knowledge beyond tool access.

Key techniques:
- Domain expertise embedded in logic
- Compliance before action
- Comprehensive documentation
- Clear governance

---

## Troubleshooting

### Skill Won't Upload

**Error: "Could not find SKILL.md in uploaded folder"**
- Solution: Rename to SKILL.md (case-sensitive)
- Verify with: `ls -la` should show SKILL.md

**Error: "Invalid frontmatter"**
- Solution: Check YAML formatting
- Ensure `---` delimiters above and below
- Check for unclosed quotes

**Error: "Invalid skill name"**
- Solution: Name has spaces or capitals
- Use kebab-case only

### Skill Doesn't Trigger

**Symptom:** Skill never loads automatically

**Fix:** Revise your description field.

Quick checklist:
- Is it too generic?
- Does it include trigger phrases users would say?
- Does it mention relevant file types?

**Debugging approach:**
Ask Claude: "When would you use the [skill name] skill?"
Claude will quote the description back. Adjust based on what's missing.

### Skill Triggers Too Often

**Symptom:** Skill loads for unrelated queries

**Solutions:**

1. Add negative triggers:
```yaml
description: Advanced data analysis for CSV files. Use for
statistical modeling, regression, clustering. Do NOT use for
simple data exploration (use data-viz skill instead).
```

2. Be more specific:
```yaml
description: Processes PDF legal documents for contract review
```

3. Clarify scope:
```yaml
description: PayFlow payment processing for e-commerce. Use
specifically for online payment workflows, not for general
financial queries.
```

### MCP Connection Issues

**Symptom:** Skill loads but MCP calls fail

Checklist:
1. Verify MCP server is connected
2. Check authentication (API keys, tokens)
3. Test MCP independently (ask Claude to call it directly)
4. Verify tool names match MCP documentation (case-sensitive)

### Instructions Not Followed

**Symptom:** Skill loads but Claude doesn't follow instructions

Common causes:
1. Instructions too verbose — keep concise, use bullet points
2. Instructions buried — put critical info at top
3. Ambiguous language — be specific
4. Model "laziness" — add explicit encouragement to be thorough

---

## Quick Checklist

### Before You Start
- [ ] Identified 2-3 concrete use cases
- [ ] Tools identified (built-in or MCP)
- [ ] Reviewed best practices
- [ ] Planned folder structure

### During Development
- [ ] Folder named in kebab-case
- [ ] SKILL.md file exists (exact spelling)
- [ ] YAML frontmatter has --- delimiters
- [ ] name field: kebab-case, no spaces, no capitals
- [ ] description includes WHAT and WHEN
- [ ] No XML tags (< >) anywhere
- [ ] Instructions are clear and actionable
- [ ] Error handling included
- [ ] Examples provided
- [ ] References clearly linked

### Before Upload
- [ ] Tested triggering on obvious tasks
- [ ] Tested triggering on paraphrased requests
- [ ] Verified doesn't trigger on unrelated topics
- [ ] Functional tests pass
- [ ] Tool integration works (if applicable)
- [ ] Compressed as .zip file

### After Upload
- [ ] Test in real conversations
- [ ] Monitor for under/over-triggering
- [ ] Collect user feedback
- [ ] Iterate on description and instructions
- [ ] Update version in metadata

---

## Resources

- **Official Docs:** Anthropic Skills Documentation
- **Public Repository:** github.com/anthropics/skills
- **Example Skills:** Document creation (PDF, DOCX, PPTX, XLSX)
- **Community:** Claude Developers Discord
