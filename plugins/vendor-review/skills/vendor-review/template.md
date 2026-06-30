# Vendor Review Document Template

Use this exact structure and heading order every time. Do not add, remove, or rename sections.

---

# Third-Party Vendor Review: [Company Name / Product Name]

*Review date: [YYYY-MM-DD]*

---

**Privacy Grade: [A / B / C / D / F]**
**Security Grade: [A / B / C / D / F]**

---

## Executive Summary

[4-5 sentences. Write about the vendor and the findings, never about the review document itself. Do not open with phrases like "This review evaluates..." or "This document assesses..." and do not describe the scope or methodology. Start directly with who the vendor is and what they make or do (or, for a product review, what the product is). Then state the most significant privacy and security takeaways, the two grades, and in plain terms what drove them. Do not make a recommendation.

Good opening: "eufy Security, a smart-home brand owned by China-based Anker Innovations, sells internet-connected cameras, doorbells, and smart locks." Bad opening: "This third-party vendor review evaluates eufy Security regarding its data privacy and security posture."]

---

## Company Overview

[Who they are, what they make or do, corporate ownership and parent company if applicable, country of incorporation and jurisdiction, and any context relevant to understanding their data handling (e.g. publicly listed, private equity backed, foreign ownership).]

---

## Privacy Policy Analysis

[Analysis of the vendor's stated privacy policy. Cover: what data is collected, retention periods, third-party sharing and data sales, opt-out rights, and any notable language that is permissive or restrictive. Reference the Mozilla Privacy Not Included review if one exists. Note any gaps between policy language and stated commitments.]

---

## Security Posture

[CVE history: list notable CVEs with ID, CVSS score, severity, and a plain-language description of what it means for a consumer. Comment on the vendor's patch cadence and resolution speed. Email authentication: summarize the vendor's posture in one plain sentence - for example, whether they enforce strong controls against domain spoofing and brand-impersonation phishing. Do not include raw DNS record syntax, record pointer chains, or technical authentication mechanics in the output document.]

---

## Breach and Regulatory History

[Describe any known data breaches or security incidents, including date, scope, and what data was exposed. Describe the vendor's response: was it prompt, transparent, and effective? Describe any regulatory actions (FTC, State AG, court settlements), including the specific findings and remedies. Assess the quality of the vendor's follow-through.]

---

## Key Risks

[Bullet list only. Include only current, unresolved risks. Never list patched CVEs or fully remediated incidents here — those belong in Security Posture or Breach and Regulatory History respectively, with their resolution or remediation noted. Rank each risk by severity using the labels below. List Severe risks first, then High, Medium, Low. Each bullet should name the risk and give a brief plain-language explanation.]

- [Severe] ...
- [High] ...
- [Medium] ...
- [Low] ...

---

## Mitigations

[Concrete mitigation steps the consumer can take. Focus on the highest-severity risks. If all risks are low, still address those. Do not make a recommendation.]

---

## Overall Assessment

[2-3 short paragraphs synthesizing the privacy and security findings into an overall picture. Do not make a recommendation.]
