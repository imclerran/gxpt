# TPRM Review: Confirmed Working Resources

## Core investigative questions

- Can this company be trusted with the consumer's data?
- Will they sell or broadly share it?
- Is it technically secure?
- **Is this vendor integrated into a broader surveillance ecosystem or mass-monitoring network?**

## Working resources

### 1. NVD CVE REST API
- Endpoint: https://services.nvd.nist.gov/rest/json/cves/2.0?keywordSearch=<vendor_or_product>
- Fetch via web__get. Returns full JSON: CVE IDs, CVSS scores and severity, descriptions, affected products (CPE), and CWE types.
- No authentication required. Always go directly to the API - web search alone undercounts CVEs.
- Read the descriptions, not just the scores. Note attack vector (NETWORK vs ADJACENT vs LOCAL) for real-world consumer impact. A medium-scored local bug can still be a serious consumer risk.
- **Determine resolution status for every CVE.** A CVE is resolved if a patch has been released:
  - Check the CPE `versionEndExcluding` or `versionEndIncluding` fields to identify the fixed firmware version.
  - Cross-check with the vendor's own security advisories, changelogs, or support pages for confirmation that a patch was released.
  - A patched CVE still belongs in the Security Posture section (with its resolution noted), but it must NOT appear in the Key Risks section unless there is evidence the patch is ineffective or not widely deployed.
  - If no patch exists or patch status cannot be confirmed, mark the CVE as unresolved.

### 2. MITRE CVE detail API
- Endpoint: https://cveawg.mitre.org/api/cve/<CVE-ID>
- Fetch via web__get for individual CVE lookups or cross-checking.

### 3. nslookup via command shell (email security posture)
- SPF:   nslookup -type=TXT <domain>
- DMARC: nslookup -type=TXT _dmarc.<domain>
- How to read it:
  - SPF ending in -all = hardfail (strong). ~all = softfail (weaker). ?all/+all or absent = weak/none.
  - DMARC p=reject or p=quarantine = enforced. p=none = monitoring only, no protection.
- The raw TXT records often reveal third-party processors via verification tokens (e.g. klaviyo-site-verification, google-site-verification). These hint at where customer data flows.
- Note: shell availability can be intermittent. If a call fails, retry once before concluding it is unavailable.

### 4. Web search and page extraction
- Breach and incident history: search company name plus terms like "breach", "leak", "exposed", "vulnerability", "security incident". Include the parent company name. Corroborate with reputable press, not forums alone.
- Vendor incident response: search for the company's public statements, blog posts, or press releases following known incidents. Note the quality, speed, and transparency of the response. A press statement alone is not sufficient evidence of genuine remediation - look for verifiable follow-through.
- Post-incident remediation: this is a distinct and important research step. Search specifically for technical or architectural changes made after an incident. Look for:
  - Shifts to local-only data storage or removal of cloud dependencies for sensitive data
  - Addition or strengthening of end-to-end encryption
  - New or expanded user-facing privacy controls (opt-in/opt-out mechanisms, permission reductions)
  - Independent third-party audits or security certifications obtained after the incident
  - Changes to data retention policies or third-party data sharing practices
  Search the company's own engineering or security blog, changelog pages, and reputable tech press coverage of what actually changed, not just what was promised. If meaningful architectural changes are confirmed, these are strong positive signals and should be prominently noted in the report.
- Regulatory and legal record: search for FTC actions, State AG settlements, class actions, congressional letters. State AG press release pages are high-value primary sources - extract the full text for specific factual findings and remedies.
- **Surveillance & Partnerships (Mandatory Step):** Search for the vendor name plus terms like "partnership," "integration," "sharing agreement," "surveillance network," "law enforcement access," "ALPR," "license plate recognition," and names of third-party public safety platforms. Do not assume a vendor is isolated; look for the ecosystem they inhabit.
- **Biometric Policies:** Search for and extract the vendor's policy on facial recognition, biometric data collection, and processing. Note if these features are local or cloud-based and whether they are opt-in.
- Privacy policy: fetch the vendor's own privacy policy page directly. Read for retention periods, third-party sharing, data sales language, and opt-out rights.
- Independent privacy reviews: Mozilla Foundation "Privacy Not Included" pages are fetchable and give a plain-language consumer read on data practices.
- Corporate ownership and jurisdiction: identify parent companies, public listing status, and country of incorporation. These affect legal exposure and data sovereignty.
- **Foreign jurisdiction data laws (Mandatory Step):** Once the country of incorporation and any foreign ownership stake are identified, assess the applicable legal regime in both directions — protective frameworks that strengthen consumer rights, and state-access or data-transfer frameworks that create risk. Do not treat this step as solely a risk check; a strong protective regime is a meaningful positive signal for the Privacy grade.

  **Protective regimes (positive signals):**
  - **EU/EEA:** GDPR imposes strict data minimisation, purpose limitation, consent requirements, right of erasure, and data breach notification. Vendors incorporated in the EU/EEA or that have submitted to GDPR enforcement are subject to meaningful regulatory oversight with substantial fines. Search the vendor name plus "GDPR compliance", "Data Protection Authority", or "DPA decision".
  - **UK:** UK GDPR and Data Protection Act 2018 are substantively equivalent to EU GDPR post-Brexit. Note whether the vendor has a UK representative and whether any ICO (Information Commissioner's Office) actions exist.
  - **Canada:** PIPEDA (and its successor Bill C-27 / Consumer Privacy Protection Act, if in force) requires meaningful consent, access rights, and accountability. Search the vendor name plus "PIPEDA", "OPC" (Office of the Privacy Commissioner), or "Privacy Commissioner of Canada".
  - **Other strong frameworks:** Switzerland (nFADP), Japan (APPI), South Korea (PIPA), Brazil (LGPD), and Australia (Privacy Act) each impose meaningful consumer-protective obligations. Note the framework and whether the vendor has faced any regulatory action under it.
  - Being subject to a strong protective regime is a positive signal for the Privacy grade, particularly when the vendor has a clean regulatory record under that framework.

  **State-access and risk regimes (negative signals):**
  - **China:** Personal Information Protection Law (PIPL), Cybersecurity Law (CSL), Multi-Level Protection Scheme (MLPS), and National Intelligence Law (NIL). Chinese-incorporated entities and entities with a controlling Chinese parent are subject to NIL Article 7, which requires cooperation with national intelligence work. Search the vendor name plus "PIPL", "Chinese ownership", "data transfer China", and the parent company name plus "National Intelligence Law".
  - **Russia:** Federal Law No. 242-FZ requires personal data of Russian users to be stored on servers physically located in Russia. Search the vendor name plus "Russia data localisation" and "Roskomnadzor".
  - **Other state-access regimes:** Note any jurisdiction with a mandatory government-access law that lacks independent judicial oversight (e.g. certain Gulf states, Belarus). Cross-reference the jurisdiction with the Freedom House "Freedom on the Net" report or analogous indices as a secondary signal.

  - Explicitly state the applicable regime and your assessment in the Privacy Policy Analysis section whether the finding is positive, negative, or neutral. Do not omit the analysis; a clean or affirmatively protective finding is itself informative.
  - Findings from this step flow into the Privacy Policy Analysis section of the document and may raise or lower the Privacy grade.

## Caveats

- Always quote exact record values, CVE IDs, and CVSS scores. Do not paraphrase primary source data.
- Prefer primary sources (APIs, government/AG pages, the company's own policy) over secondary summaries.
- A small or obscure company with no CVE or regulatory record should be noted as a neutral finding - absence of data is not the same as a clean bill of health, nor is it an automatic negative.
- Be explicit in the final report about what was and was not accessible. Do not describe a failed tool call as a denial unless it actually was one.
- Past breaches or CVEs are not necessarily an indicator of future risk. Prompt resolution of CVEs and strong, transparent remediation after incidents are positive signals and should be weighed accordingly.
