from datetime import datetime
import html
import json
import os


SECURITY_TOOLS = {
    "url_parse",
    "dns_lookup",
    "tcp_connect_scan",
    "http_probe",
    "security_headers",
    "web_fingerprint",
    "redirect_chain",
    "http_methods",
    "cookie_audit",
    "cors_check",
    "web_crawl",
    "path_probe",
    "tls_info",
    "jwt_decode",
    "sql_injection_detect",
    "xss_detect",
    "subdomain_enum",
    "cve_lookup",
    "secret_scan",
    "nmap_scan",
    "nuclei_scan",
    "ffuf_fuzz",
    "sqlmap_scan",
    "gitleaks_scan",
    "httpx_external_probe",
    "caido_start",
}


def _safe_text(value, limit=1200):
    text = value if isinstance(value, str) else json.dumps(value, ensure_ascii=False, default=str)
    text = text.strip()
    if len(text) > limit:
        return text[:limit] + "...[truncado]"
    return text


def _severity(record):
    result = record.get("result", {}) if isinstance(record, dict) else {}
    if not result.get("ok"):
        return "info"
    name = record.get("name", "")
    if name in {"secret_scan", "gitleaks_scan"} and int(result.get("secrets_found") or result.get("leak_count") or 0) > 0:
        return "high"
    if name in {"nuclei_scan", "sqlmap_scan"} and int(result.get("finding_count") or result.get("result_count") or 0) > 0:
        return "high"
    if result.get("vulnerable") or result.get("findings"):
        return "medium"
    return "info"


def build_markdown_report(records, workspace_root, title="NEXT-IO audit report"):
    now = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    security_records = [item for item in records if item.get("name") in SECURITY_TOOLS]
    lines = [
        f"# {title}",
        "",
        f"- Generated: {now}",
        f"- Workspace: `{workspace_root}`",
        f"- Security tool runs: {len(security_records)}",
        "",
        "## Executive summary",
        "",
    ]
    if not security_records:
        lines.append("No security-focused tool runs were recorded in this session.")
    else:
        counts = {"high": 0, "medium": 0, "info": 0}
        for record in security_records:
            counts[_severity(record)] += 1
        lines.extend(
            [
                f"- High signal findings: {counts['high']}",
                f"- Medium signal findings: {counts['medium']}",
                f"- Informational observations: {counts['info']}",
            ]
        )

    lines.extend(["", "## Evidence", ""])
    for index, record in enumerate(security_records, 1):
        result = record.get("result", {})
        lines.extend(
            [
                f"### {index}. {record.get('name', 'tool')}",
                "",
                f"- Severity: `{_severity(record)}`",
                f"- OK: `{result.get('ok')}`",
            ]
        )
        for key in ("url", "host", "target", "domain", "software", "version", "status_code", "finding_count", "result_count", "secrets_found", "error"):
            if key in result:
                lines.append(f"- {key}: `{_safe_text(result.get(key), 240)}`")
        lines.extend(["", "```json", _safe_text(result, 3000), "```", ""])

    return "\n".join(lines).rstrip() + "\n"


def build_html_report(markdown):
    escaped = html.escape(markdown)
    return (
        "<!doctype html><html><head><meta charset=\"utf-8\">"
        "<title>NEXT-IO audit report</title>"
        "<style>body{font-family:Segoe UI,Arial,sans-serif;max-width:980px;margin:32px auto;padding:0 20px;line-height:1.5}"
        "pre{background:#111;color:#eee;padding:14px;overflow:auto;border-radius:6px}"
        "code{background:#eee;padding:2px 4px;border-radius:4px}</style></head><body>"
        f"<pre>{escaped}</pre></body></html>"
    )


def write_reports(records, workspace_root, output_dir="reports"):
    base_dir = os.path.join(workspace_root, output_dir)
    os.makedirs(base_dir, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    md_path = os.path.join(base_dir, f"audit-{stamp}.md")
    html_path = os.path.join(base_dir, f"audit-{stamp}.html")
    markdown = build_markdown_report(records, workspace_root)
    with open(md_path, "w", encoding="utf-8") as file:
        file.write(markdown)
    with open(html_path, "w", encoding="utf-8") as file:
        file.write(build_html_report(markdown))
    return md_path, html_path
