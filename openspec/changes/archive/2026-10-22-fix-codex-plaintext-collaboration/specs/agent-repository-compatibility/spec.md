## ADDED Requirements

### Requirement: Ship review separates findings from evidence confirmation
The ship status check SHALL keep positive suppressed, open, and previously-missed
issue counts blocking, regardless of a clean overview headline. It SHALL report
only the recognized evidence-only closer-look overview as a separate
`COPILOT_EVIDENCE_CONFIRMATION` signal. Unknown closer-look bodies, additional
unclassified text, unresolved inline threads, and API failures MUST NOT become an
all-clear. Shipping still requires inspecting actual real-client execution evidence.

#### Scenario: Clean overview requests client-evidence confirmation
- **WHEN** the latest current-head Copilot review consists only of the known evidence-confirmation overview and `No open issues`
- **THEN** the status reports zero body findings and a positive evidence-confirmation signal
- **AND** the operator still verifies the actual client-owned execution evidence before merging

#### Scenario: Clean headline accompanies a body finding
- **WHEN** a review contains a positive suppressed or previously-missed issue count beside `No open issues`
- **THEN** the body finding remains blocking

#### Scenario: Closer-look wording is not recognized
- **WHEN** a closer-look review has an unknown concern or extra unclassified text
- **THEN** the status retains a blocking body finding rather than infer that the review is clean
