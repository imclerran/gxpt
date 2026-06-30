---
name: Third-Party Vendor Review
description: Use this when the user wants to draft a third-party vendor or product privacy and security review document for consumer use.
---

# Third-Party Vendor Review

This skill produces a consumer-focused TPRM review document in markdown.  The document is neutral and analytical in tone, concise (3-5 pages of findings), and organized into a fixed set of section headings. The primary questions are always: can this vendor be trusted with the consumer's data, do they respect privacy, and is their security posture sound?

## Grade definitions (internal - do not print in the output document)

These definitions inform your grading judgment. Do not include a grade key in the output.

**Privacy grade:**
- A - Strong, transparent privacy policy; no data sales; meaningful opt-out rights; minimal third-party sharing; clear retention limits.
- B - Adequate privacy policy with minor gaps; limited or non-material third-party sharing; opt-out available but not prominent.
- C - Policy is vague or broad; some data sharing or sale language; warrants consumer awareness; limited warrantless access for emergencies only.
- D - Policy permits significant data sales or broad sharing; weak or absent opt-out; active participation in broader surveillance networks (e.g. sharing with third-party monitoring platforms or data brokers); history of significant internal data misuse even if partially remediated.
- F - Actively deceptive or absent privacy policy; confirmed data sales with no recourse; serious regulatory findings for privacy violations; consistent record of warrantless data disclosure; partnerships that facilitate automated mass surveillance without explicit user opt-in/opt-out.

**Security grade:**
- A - No meaningful CVEs or all resolved promptly; strong email authentication (SPF hardfail + DMARC enforce); no unresolved breaches; strong incident response track record.
- B - Minor or low-severity CVEs; adequate email authentication; breach history present but well-remediated with demonstrated follow-through.
- C - Moderate CVEs or slow patching; partial email authentication; breach or incident history with adequate but unremarkable response.
- D - High or critical CVEs unresolved or slowly resolved; weak email authentication; breach history with poor or incomplete response.
- F - Critical unresolved vulnerabilities; no email authentication; serious unresolved breach or confirmed ongoing security failures.

Vendor response to incidents is a named factor in the security grade. Strong, transparent, and effective remediation after a breach or CVE is a positive signal and can raise a grade. Dismissive, slow, or deceptive response is a negative signal and can lower one.

Verified post-incident architectural improvements are among the strongest positive signals available. If a vendor demonstrably changed how data is stored, transmitted, or controlled following an incident - for example by moving to local-only storage, adding end-to-end encryption, or removing cloud dependencies for sensitive data - this should be weighted heavily in the security grade and noted prominently in the report. A press statement without verifiable follow-through carries little weight.

## Step 1 - Clarify the scope

Read the user's request and determine:
- Is this a company-level review, a specific product review, or both?
- What is the primary domain of the company or product?
- Are there parent companies or related entities worth noting?

If the request is ambiguous, ask before proceeding.

## Step 2 - Conduct the investigation

Read `resources.md` in this skill folder before beginning research. It contains the confirmed working tools, endpoints, and search techniques to use for this investigation.

**Web research agent:** If the `web-research` agent is available, delegate multi-step web research tasks to it via `dispatch_agent`. The agent has access to `web__search`, `web__extract`, and `web__get`. Give each dispatched task a complete, self-contained description - include the vendor name, the specific questions to answer, and the investigative techniques from `resources.md` that apply.

Dispatch in two waves to respect dependencies:

**First wave - dispatch in parallel, no dependencies:**
- Breach and incident history (what incidents, CVEs, and exposures are on record)
- Privacy policy analysis (fetch and analyse the vendor's privacy policy)
- Corporate ownership and jurisdiction (parent companies, country of incorporation, ownership stake)
- Surveillance partnerships (third-party sharing agreements, integrations with monitoring platforms)
- Independent privacy reviews (Mozilla Privacy Not Included and similar)
- Regulatory and legal record (FTC actions, State AG settlements, class actions)

Wait for first-wave results before dispatching the second wave.

**Second wave - dispatch in parallel, depends on first wave:**
- Vendor incident response and post-incident remediation (requires knowing which incidents were found in the first wave - include those specifics in the task description)
- Foreign jurisdiction data law analysis (requires the jurisdiction and ownership findings from the first wave - include those specifics in the task description; analyse in both directions: protective regimes and state-access risk regimes)

Do not delegate single-item direct fetches or tasks that cannot be accomplished with those three web tools: the NVD CVE API call, the MITRE CVE detail API call, and the nslookup DNS checks must be run directly (they use a command shell or a single known URL, not open-ended web research). These direct calls may run concurrently with the first wave.

If the `web-research` agent is not available, perform all research steps directly using the techniques in `resources.md`.

Follow the investigative steps in that file. At minimum, cover:
- CVE lookup via the NVD API
- Email authentication posture via nslookup (SPF and DMARC) - use raw records internally to inform the security grade only; do not reproduce record syntax in the output document. Note any third-party processors discovered during DNS investigation and carry them forward to the Privacy Policy Analysis section
- Breach and incident history via web search and source extraction
- Vendor incident response: quality, speed, and transparency of public response
- Post-incident remediation: verified technical or architectural changes made after incidents (this is a distinct research step - do not skip it)
- Regulatory and legal record via web search and source extraction
- Privacy policy via direct fetch of the vendor's policy page. If DNS record investigation reveals downstream third-party processors (such as CRM, billing, or marketing platforms), list them in the Privacy Policy Analysis section as a data-sharing disclosure, not in the Security Posture section
- Independent privacy reviews if available (e.g. Mozilla Privacy Not Included)
- Corporate ownership and jurisdiction
- **Foreign Jurisdiction Data Laws:** Analysis of the data law regime governing the vendor's jurisdiction of incorporation and any parent company's jurisdiction. This cuts both ways: some regimes impose state-access, mandatory retention, or cross-border transfer obligations that create risk (e.g. China's PIPL/NIL, Russia's data localisation law); others impose strong consumer-protective obligations (e.g. EU GDPR, UK GDPR, Canada's PIPEDA) that are a positive signal. Identify the country of incorporation, any foreign ownership stake, and assess whether the applicable legal framework strengthens or weakens consumer data protection.
- **Partnerships & Ecosystems:** Third-party sharing agreements, integrations with surveillance platforms, and data-sharing networks.

Collect the raw findings before drafting. Quote exact CVE IDs, CVSS scores, DNS record values, and regulatory finding language. Do not paraphrase primary source data.

## Step 3 - Assign grades

Using the definitions above and the collected findings, assign:
- A single letter Privacy grade (A through F)
- A single letter Security grade (A through F)

Be prepared to justify each grade from the findings in the body of the document.

## Step 4 - Draft the document

Read `template.md` in this skill folder before drafting. It contains the exact section headings and formatting conventions to follow.

Keep the document to 3-5 pages of findings (excluding the grades and executive summary). Write for a mixed audience: one reader is a software developer, the other is a non-technical TPRM manager. Technical details (CVE IDs, CVSS scores, DNS records) are acceptable but should not dominate the prose. Plain-language interpretation must accompany any technical finding.

**Key Risks section rules:** The Key Risks section lists only current, unresolved risks. Do not list:
- CVEs that have been patched (confirmed via NVD CPE version data or vendor advisories).
- Incidents or breaches that have been fully remediated with verified architectural or policy changes.
- Historical problems that are no longer exploitable.

A patched CVE belongs in Security Posture with its resolution noted. A remediated incident belongs in Breach and Regulatory History with its follow-up described. Neither belongs in Key Risks. Slow or absent patching, or ineffective remediation, is itself a current risk and may be listed.

Do not make a recommendation. The grades and findings speak for themselves.

## Step 5 - Review before delivering

Before delivering the document, check:
- Both letter grades are displayed above the executive summary
- Executive summary is 4-5 sentences
- All eight sections are present and in order: Executive Summary, Company Overview, Privacy Policy Analysis, Security Posture, Breach and Regulatory History, Key Risks, Mitigations, Overall Assessment
- Key Risks are bullet points ranked by severity (Severe, High, Medium, Low)
- Mitigations focus on the highest-severity risks; if all risks are low, mitigations still address them
- Overall Assessment is the final section
- The Breach and Regulatory History section addresses both the incident response and any verified post-incident remediation - if the vendor made meaningful architectural or policy changes after an incident, these are explicitly described
- Foreign jurisdiction data law analysis is present in the Privacy Policy Analysis section for any vendor, regardless of jurisdiction - analysis runs in both directions: protective regimes (e.g. EU GDPR, UK GDPR, Canada PIPEDA) are a positive signal and must be noted, not just state-access or data-transfer risk regimes
- No recommendation language appears anywhere
- Key Risks contain only current, unresolved risks — no patched CVEs and no fully remediated incidents appear in this section
- Length is within bounds (3-5 pages of findings)
- All primary source data (CVE IDs, scores, DNS records, regulatory quotes) is quoted exactly
