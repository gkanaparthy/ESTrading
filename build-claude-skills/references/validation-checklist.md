# Skill Validation & Testing Checklist

Step-by-step validation guide to ensure your skill is production-ready.

## Pre-Development Checklist

Before you start coding, verify you have clarity on:

- [ ] **Use Cases Defined**
  - [ ] At least 2-3 concrete use cases documented
  - [ ] Each use case has: trigger phrase, steps, expected result
  - [ ] Use cases are realistic and achievable

- [ ] **Tools Identified**
  - [ ] Built-in Claude capabilities reviewed (file processing, code, etc.)
  - [ ] MCP servers identified if needed
  - [ ] Tool permissions/scope verified

- [ ] **Success Metrics Defined**
  - [ ] Quantitative metrics (triggers, tool calls, error rates)
  - [ ] Qualitative metrics (user experience, consistency)
  - [ ] Clear pass/fail criteria

## During Development Checklist

### Folder Structure
- [ ] Folder named in kebab-case (lowercase, hyphens only)
- [ ] Folder name matches skill `name` field
- [ ] Optional subdirectories created as needed:
  - [ ] `scripts/` (if you have code)
  - [ ] `references/` (if you have detailed docs)
  - [ ] `assets/` (if you have templates)

### SKILL.md File
- [ ] File exists with exactly this name: `SKILL.md` (case-sensitive)
- [ ] File starts with `---` (YAML delimiter)
- [ ] YAML frontmatter includes required fields:
  - [ ] `name`: kebab-case, no spaces/caps/underscores
  - [ ] `description`: Includes WHAT + WHEN + trigger phrases
  - [ ] `---` (closing delimiter)

### Frontmatter Validation
- [ ] No XML tags (< >) anywhere in frontmatter
- [ ] All quotes properly closed
- [ ] No leading spaces before `-` in YAML
- [ ] Proper indentation (2 spaces, not tabs)
- [ ] No secrets or API keys in frontmatter

### Content Validation
- [ ] Description is specific (not generic like "helps with projects")
- [ ] Description under 1024 characters
- [ ] Trigger phrases included in description
- [ ] File types mentioned if relevant
- [ ] Instructions are clear and actionable
- [ ] Examples provided with expected outcomes
- [ ] Error handling sections included
- [ ] References to documentation files properly linked
- [ ] Progressive disclosure applied (detailed docs in references/)

### Code Quality (if applicable)
- [ ] Scripts in `scripts/` folder are executable
- [ ] Scripts have clear comments
- [ ] Error messages are user-friendly
- [ ] Scripts validate inputs before processing
- [ ] Temporary files cleaned up

### Documentation Quality
- [ ] Main SKILL.md under 5000 words
- [ ] Detailed docs in `references/` directory
- [ ] Links between files work correctly
- [ ] No broken markdown syntax
- [ ] Code blocks properly formatted with language tags

## Pre-Upload Testing Checklist

### Manual Testing
- [ ] Upload skill to Claude.ai or Claude Code
- [ ] Verify upload succeeds (no error messages)

### Triggering Tests
Test that skill loads when it should:

- [ ] **Test 1: Obvious trigger**
  - Ask Claude: "Help me [specific task from use case 1]"
  - Verify: Skill loads automatically
  - Document: Worked / Didn't work

- [ ] **Test 2: Paraphrased request**
  - Ask Claude: Different phrasing of same task
  - Verify: Skill loads automatically
  - Document: Worked / Didn't work

- [ ] **Test 3: Multiple triggers**
  - Test at least 3 different ways to phrase the request
  - Verify: Skill loads for all variations
  - Success rate: ≥80% (at least 2 out of 3)

Test that skill does NOT load when it shouldn't:

- [ ] **Test 4: Unrelated topic**
  - Ask Claude: Something completely different
  - Verify: Skill does NOT load
  - Document: Correct

- [ ] **Test 5: Similar but different service**
  - Ask Claude: Request for different tool/service
  - Verify: Skill does NOT load
  - Document: Correct

- [ ] **Test 6: Generic request**
  - Ask Claude: Vague request that could apply to many skills
  - Verify: Skill does NOT load
  - Document: Correct

### Functional Tests
- [ ] Skill loads and displays instructions properly
- [ ] All code examples are correct
- [ ] Links to reference files work
- [ ] MCP calls (if any) succeed or fail gracefully
- [ ] Error messages are clear and actionable

### Edge Case Testing (if applicable)
- [ ] Test with minimal/incomplete input
- [ ] Test with maximum/very large input
- [ ] Test with special characters in input
- [ ] Test with empty/null values
- [ ] Test network timeouts (if MCP-dependent)

## Post-Upload Monitoring

### First 24 Hours
- [ ] Skill appears in Claude settings
- [ ] Manual enable/disable works
- [ ] No obvious errors in initial use
- [ ] Triggering behavior as expected

### First Week
- [ ] Use skill in real workflows
- [ ] Collect feedback from test users
- [ ] Monitor for under-triggering signals
- [ ] Monitor for over-triggering signals
- [ ] Document any issues

### Iteration Based on Feedback

**If undertriggering (skill doesn't load when it should):**
- [ ] Review description field
- [ ] Add more trigger phrases
- [ ] Include common keywords
- [ ] Be more specific about use cases

**If overtriggering (skill loads for wrong requests):**
- [ ] Add negative triggers to description
- [ ] Be more specific/less generic
- [ ] Clarify scope
- [ ] Remove overly broad keywords

**If execution issues:**
- [ ] Review and improve instructions
- [ ] Add more error handling examples
- [ ] Clarify ambiguous language
- [ ] Add additional validation steps

## Quality Scoring

Rate your skill on each criterion (1-5):

- [ ] **Clarity of Description:** 1 2 3 4 5
  - (1 = vague, 5 = crystal clear with specific triggers)

- [ ] **Completeness of Instructions:** 1 2 3 4 5
  - (1 = minimal, 5 = comprehensive with examples)

- [ ] **Error Handling:** 1 2 3 4 5
  - (1 = none, 5 = handles common issues)

- [ ] **User Experience:** 1 2 3 4 5
  - (1 = confusing, 5 = intuitive and helpful)

- [ ] **Documentation Quality:** 1 2 3 4 5
  - (1 = poor formatting, 5 = professional and well-organized)

**Target Score:** 20+ out of 25 for release-ready skill

## Pre-Release Verification

Final checklist before sharing:

- [ ] All 4 triggering tests passed (≥80% success)
- [ ] All 3 non-trigger tests passed (should NOT load)
- [ ] All functional tests passed
- [ ] Quality score ≥20/25
- [ ] No security issues (no secrets, no injection risks)
- [ ] Folder structure correct
- [ ] SKILL.md is valid (can be parsed as YAML + Markdown)
- [ ] All file links work correctly
- [ ] Scripts are executable (if applicable)
- [ ] README created (if distributing via GitHub)
- [ ] Installation instructions provided
- [ ] Example usage documented with screenshots (if applicable)

## Common Issues & Fixes

### Upload Fails: "Could not find SKILL.md"
**Fix:** Check file name and case sensitivity
```bash
# Verify from command line
ls -la your-skill-folder/
# Should show: SKILL.md (exact case)
```

### Skill Never Triggers
**Fix:** Review description field
```
❌ Bad: "Helps with projects"
✅ Good: "Manages Linear project sprints. Use when user mentions sprint, create tasks, or plan sprint."
```

### Skill Triggers Constantly
**Fix:** Add negative triggers and be more specific
```yaml
description: Data analysis for CSV files. Use for statistical analysis.
Do NOT use for simple spreadsheet viewing.
```

### Instructions Not Followed
**Fix:** Make them more specific and add validation
```
❌ Bad: "Validate the data before proceeding"
✅ Good: "CRITICAL: Before calling create_project, verify:
- Project name is non-empty
- At least one team member assigned
- Start date is not in the past"
```

### Frontmatter Parse Error
**Fix:** Validate YAML syntax
```yaml
# ❌ Wrong - missing delimiters
name: my-skill
description: Does stuff

# ✅ Correct
---
name: my-skill
description: Does stuff
---
```

## Testing Tools

### Manual YAML Validation
```bash
# If you have yamllint installed
yamllint SKILL.md

# Or use Python
python3 -c "import yaml; yaml.safe_load(open('SKILL.md'))"
```

### Check for Common Issues
```bash
# Look for XML tags (forbidden)
grep -n "[<>]" SKILL.md

# Check file name
ls -la SKILL.md

# Verify kebab-case folder name
ls -d */ | grep -v "^[a-z0-9-]*/$"
```

## Rollback Plan

If issues discovered after upload:

1. [ ] Document the issue
2. [ ] Identify root cause
3. [ ] Create updated version
4. [ ] Test thoroughly
5. [ ] Re-upload with updated version number
6. [ ] Notify users of fix (if shared)

---

## Sign-Off

Before declaring a skill complete:

- [ ] All checklists above: PASSED
- [ ] Tested by: ______________________
- [ ] Date tested: ______________________
- [ ] Any remaining issues: ______________________
- [ ] Ready for release: YES / NO

