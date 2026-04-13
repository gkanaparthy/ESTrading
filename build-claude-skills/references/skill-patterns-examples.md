# Skill Design Patterns & Examples

Detailed examples of common skill patterns and how to implement them.

## Pattern 1: Sequential Workflow Orchestration

**Use when:** Multi-step processes must execute in a specific order.

**Example:** Customer Onboarding

```markdown
# Customer Onboarding Skill

## Workflow: Complete Customer Setup

### Step 1: Create Account
- Collect: Name, email, company
- Call MCP tool: `create_customer`
- Validation: Email must be unique
- Expected output: Customer ID

### Step 2: Setup Payment Method
- Call MCP tool: `setup_payment_method`
- Params: Customer ID (from Step 1), payment details
- Wait for: Payment verification (may take 30 seconds)
- Validation: Must receive confirmation code
- If fails: Provide retry instructions

### Step 3: Create Subscription
- Call MCP tool: `create_subscription`
- Params: Customer ID, plan_id, payment_method_id
- Validation: Subscription must be active status
- Expected output: Subscription confirmation

### Step 4: Send Welcome Email
- Call MCP tool: `send_email`
- Template: welcome_email_template
- Params: Customer email (from Step 1)
- Confirmation: Email delivery must succeed

## Error Handling & Rollback

If Step 2 fails:
1. Log the error with customer ID
2. Advise user of issue
3. Provide manual escalation path

If Step 3 fails:
1. Payment method exists but subscription creation failed
2. Check payment status first
3. Retry subscription creation
4. If still fails, manual review needed

## Checklist Before Declaring Success

- [ ] Customer account created and active
- [ ] Payment method verified
- [ ] Subscription is active
- [ ] Welcome email delivered
- [ ] No error logs generated
```

**Key Techniques:**
- Explicit ordering with numbered steps
- Dependencies between steps clearly stated
- Validation checks at each stage
- Clear error recovery procedures

---

## Pattern 2: Multi-MCP Coordination

**Use when:** Workflow spans multiple services that need to coordinate.

**Example:** Design-to-Development Handoff

```markdown
# Design-to-Development Handoff

## Phase 1: Design Export (Figma MCP)

1. User uploads Figma file or provides link
2. Call `figma.export_design_specs`
   - Returns: Design file, asset list, component specs
3. Call `figma.generate_component_guide`
   - Returns: Markdown guide with design tokens
4. Validate: All colors, fonts, spacing documented

## Phase 2: Asset Storage (Google Drive MCP)

1. Call `drive.create_folder`
   - Name: "Design Assets - [Project Name]"
   - Parents: Design Hub folder
   - Returns: Folder ID
2. Call `drive.upload_files`
   - Files: All exported assets from Phase 1
   - Folder ID: From step above
   - Returns: Shareable links for each asset
3. Create asset manifest JSON
   - Structure: { "colors": [...], "icons": [...], "fonts": [...] }
   - Save to Drive

## Phase 3: Task Creation (Linear MCP)

1. Parse design components into tasks
2. Call `linear.create_team`
   - Name: "[Project] Design Handoff"
   - Returns: Team ID
3. For each component:
   - Call `linear.create_issue`
   - Title: Component name
   - Description: Design specs + link to asset
   - Link: Asset URL from Phase 2
   - Custom field: "design_reference_link"
4. Call `linear.bulk_assign`
   - Issues: All created tasks
   - Team: Engineering team
   - Returns: Confirmation

## Phase 4: Notification (Slack MCP)

1. Call `slack.get_engineering_channel`
2. Call `slack.post_message`
   - Text: "Design handoff ready for [Project]"
   - Blocks: Links to Design folder + Linear team
   - Thread: Links to all individual task cards

## Validation Points

- [ ] All design assets in Drive
- [ ] All assets have shareable links
- [ ] All Linear tasks have asset references
- [ ] Slack notification posted
- [ ] No missing components

## Error Handling

**If Figma export fails:**
- Log error with file ID
- Ask user to verify file permissions
- Suggest manual export as fallback

**If Drive upload fails:**
- Check file size limits
- Verify storage quota
- Retry with smaller batches

**If Linear creation fails:**
- Validate team permissions
- Check for duplicate issue names
- Manual task creation fallback
```

**Key Techniques:**
- Clear phase separation
- Data passing between services (e.g., Folder ID from Phase 2 → Links in Phase 3)
- Validation gates between phases
- Comprehensive error handling for each service

---

## Pattern 3: Iterative Refinement

**Use when:** Output quality improves through multiple iterations (reports, documents, code).

**Example:** Report Generation with Quality Assurance

```markdown
# Iterative Report Generation

## Phase 1: Initial Draft

1. Fetch data via MCP call: `fetch_quarterly_metrics`
   - Returns: Sales, margins, customer metrics
   - Range: Current quarter
2. Generate initial report
   - Structure: Executive summary + 4 sections
   - Include: Charts, tables, key insights
3. Save to temporary file
   - Format: Markdown with embedded charts
   - Validate: All required sections present

## Phase 2: Quality Check

Run validation script: `scripts/check_report.py`

This checks for:
- Missing sections (all 6 required?)
- Incomplete data (all numbers populated?)
- Inconsistent formatting (headers, spacing)
- Data validation errors (negative percentages, etc)
- Chart generation success

Returns: List of issues with severity (ERROR, WARNING, INFO)

## Phase 3: Refinement Loop

For each identified issue:

**If ERROR:**
1. Stop. Requires manual intervention.
2. Log details.
3. Ask user for clarification.

**If WARNING:**
1. Regenerate affected section
2. Re-run validation on that section only
3. If still warning, log and continue

**If INFO:**
1. Optional improvement
2. If time budget allows, regenerate
3. If not, note in metadata and continue

Continue until:
- Zero ERRORs
- Less than 2 WARNINGs
- Zero INFO items (or time budget exhausted)

## Phase 4: Finalization

1. Apply final formatting
   - Consistent fonts and colors
   - Add page numbers and headers
   - Ensure PDF rendering quality
2. Generate executive summary
3. Add table of contents
4. Save final version
   - Format: PDF + source Markdown
   - Location: Report archive folder

## Success Criteria

- [ ] All sections present and complete
- [ ] No data validation errors
- [ ] Formatting consistent throughout
- [ ] Charts render correctly
- [ ] No more than 2 warnings
- [ ] PDF successfully generated
```

**Key Techniques:**
- Explicit quality criteria (ERRORS vs WARNINGS)
- Automated validation scripts
- Iterative regeneration of problem areas
- Clear stopping conditions (avoid infinite loops)
- Knowledge of when to stop improving

---

## Pattern 4: Context-Aware Tool Selection

**Use when:** Same outcome but different tools depending on input context.

**Example:** Smart File Storage Decision

```markdown
# Smart File Storage Decision Engine

## Decision Tree

1. Check file type and size
2. Determine optimal storage:

### Large Files (>10MB)
- Best: Cloud storage MCP (Google Drive, AWS S3)
- Reason: Cost-effective, scalable, reliable
- Implementation:
  - Call `cloud_storage.upload_large_file`
  - Generate: Shareable link
  - Return: Link + expiration date

### Collaborative Docs (Word, Sheets, Slides)
- Best: Notion/Google Docs MCP
- Reason: Real-time collaboration, comments
- Implementation:
  - Call `docs_mcp.create_doc`
  - Set: Sharing permissions
  - Return: Editable link

### Code Files (.py, .js, .ts)
- Best: GitHub MCP
- Reason: Version control, CI/CD integration
- Implementation:
  - Call `github.create_gist` (or create file in repo)
  - Set: Language highlighting
  - Return: GitHub link

### Temporary Files (<1MB, temporary)
- Best: Local storage or temporary cache
- Reason: No setup needed, no cost
- Implementation:
  - Save to temp directory
  - Set: Auto-delete after 24 hours
  - Return: Local path (expires message)

### Archive Files (.zip, .tar, .gz)
- Best: Object storage (S3, GCS)
- Reason: Efficient compression, reliable retrieval
- Implementation:
  - Call `archive_storage.upload`
  - Set: Lifecycle policy for old files
  - Return: Download link

## Provide Transparency

Always tell the user WHY their file went somewhere:

```
I'm saving your large dataset to Google Drive because:
1. File size (15MB) exceeds local storage limits
2. You'll likely need to share it with team members
3. Google Drive provides better access control than email

Link: [shareable link]
Expires: [date]
```

## Error Handling

**If primary option fails:**
- Fallback: Try secondary option
- Example: If Drive upload fails, try Google Cloud Storage
- Inform user: "Using Cloud Storage as fallback"

**If all options fail:**
- Provide manual options
- Example: "Unable to upload. Please email file to yourself or use Dropbox."
```

**Key Techniques:**
- Clear decision criteria (file type, size, duration)
- Explicit tool selection logic
- Fallback options for each case
- User communication about choices

---

## Pattern 5: Domain-Specific Intelligence

**Use when:** Skill adds specialized knowledge beyond tool access.

**Example:** Financial Compliance Checking

```markdown
# Payment Processing with Compliance

## Before Processing: Compliance Validation

### Step 1: Fetch Transaction Details
Call MCP: `fetch_transaction`
- Returns: Amount, parties, jurisdiction, transaction type

### Step 2: Apply Compliance Rules

#### Check 1: Sanctions List Screening
- Query: US OFAC sanctions database
- Check: Both sender and recipient against list
- Action if found: BLOCK transaction, notify compliance team
- Logging: Log with transaction ID and reason

#### Check 2: Jurisdiction Verification
- Rule: Certain countries restricted for this business
- Check: Both parties' countries of residence
- Permitted countries: US, CA, UK, EU, AU, NZ
- Action if violated: FLAG for review, don't process
- Override: Compliance manager can approve

#### Check 3: Risk Level Assessment
- Calculate: Based on amount, frequency, jurisdiction
- High risk: >$100K, first-time sender, high-risk country
- Medium risk: >$50K, repeat sender
- Low risk: <$50K, trusted sender
- Action: Route to appropriate review queue

#### Check 4: KYC/AML Verification
- Check: Customer KYC status is current
- Requirement: Must be updated within 12 months
- If expired: BLOCK, ask for KYC update
- Logging: Document KYC status and dates

### Step 3: Document Compliance Decision

Create compliance record:
```
{
  "transaction_id": "TXN_123456",
  "compliance_checks": {
    "sanctions": "PASS",
    "jurisdiction": "PASS",
    "risk_level": "MEDIUM",
    "kyc": "PASS"
  },
  "overall_decision": "APPROVED_WITH_REVIEW",
  "approved_by": "system",
  "timestamp": "2024-02-20T10:30:00Z",
  "audit_trail": [...]
}
```

## Processing (If Compliance Passed)

1. Call MCP: `process_payment`
   - Amount, parties, details
2. Apply fraud detection checks
3. Execute transaction
4. Update compliance record with result

## If Compliance Failed

1. Flag transaction for manual review
2. Create compliance case
3. Notify compliance officer
4. Store full audit trail
5. Send notification to user (if allowed)

## Audit Trail Requirements

Every compliance decision must log:
- What rule was checked
- Result (PASS/FAIL)
- Timestamp
- Who/what made the decision
- Any override approvals
- Final decision

This enables:
- Regulatory audits
- Dispute resolution
- Training on false positives
- Continuous improvement
```

**Key Techniques:**
- Domain expertise embedded in rule engine
- Compliance BEFORE action (fail-safe)
- Comprehensive audit logging
- Clear governance (who can override)
- Multiple validation layers

---

## Choosing Your Pattern

| Pattern | Use Case | Example |
|---------|----------|---------|
| Sequential Orchestration | Multi-step ordered process | Onboarding, deployment |
| Multi-MCP Coordination | Data flows between services | Design handoff, CI/CD |
| Iterative Refinement | Quality improves with rework | Report generation, code review |
| Context-Aware Selection | Same goal, different paths | File storage, routing |
| Domain Intelligence | Specialized knowledge needed | Compliance, medical, legal |

**Pro tip:** Many skills combine multiple patterns. For example, a "design handoff" skill uses Multi-MCP Coordination (Figma → Drive → Linear) with Iterative Refinement (design review loop) and Context-Aware Selection (choosing storage based on file type).
