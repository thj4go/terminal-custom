import copy
import os
import re


CORE_TOOLS = {
    "list_dir",
    "find_files",
    "search_text",
    "read_file",
    "read_file_lines",
    "file_info",
    "hash_file",
    "run_terminal",
}

WRITE_TOOLS = {
    "create_folder",
    "create_file",
    "append_text",
    "replace_text",
    "apply_patch",
    "copy_path",
    "move_path",
    "delete_path",
}

BINARY_TOOLS = {
    "read_binary",
    "write_binary",
    "hexdump",
    "extract_strings",
    "base64_encode_file",
    "base64_decode_file",
    "encode_decode",
}

WEB_SECURITY_TOOLS = {
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
}

PENTEST_TOOLS = WEB_SECURITY_TOOLS | {
    "jwt_decode",
    "sql_injection_detect",
    "xss_detect",
    "subdomain_enum",
    "cve_lookup",
    "secret_scan",
    "pentest_tool_status",
    "nmap_scan",
    "nuclei_scan",
    "ffuf_fuzz",
    "sqlmap_scan",
    "gitleaks_scan",
    "httpx_external_probe",
    "caido_start",
}


def compact_tools_enabled():
    return os.getenv("NEXT_IO_COMPACT_TOOLS", "true").strip().lower() in {"1", "true", "yes", "sim", "on"}


def route_tools(user_input, web_needed=False):
    text = str(user_input or "").lower()
    selected = set(CORE_TOOLS)
    if re.search(r"\b(crie|edite|altere|corrija|patch|arquivo|commit|teste|rodar|executar|instalar|delete|apague|mova|copie)\b", text):
        selected |= WRITE_TOOLS
    if re.search(r"\b(binario|binary|base64|hex|hexdump|hash|strings|payload|jwt)\b", text):
        selected |= BINARY_TOOLS
    if web_needed or re.search(r"\b(pentest|scan|nmap|nuclei|ffuf|sqlmap|xss|sqli|cve|tls|dns|http|url|subdominio|cookie|cors|header|vulnerab|secret|gitleaks|caido|proxy|intercept)\b", text):
        selected |= PENTEST_TOOLS
    return selected


def compact_tool(tool):
    if not compact_tools_enabled():
        return tool
    item = copy.deepcopy(tool)
    function = item.get("function", {})
    if "description" in function:
        function["description"] = function["description"].split(".")[0][:120]
    properties = function.get("parameters", {}).get("properties", {})
    for spec in properties.values():
        if isinstance(spec, dict) and "description" in spec:
            spec.pop("description", None)
    return item


def filter_tools(tools, names):
    allowed = set(names or [])
    return [compact_tool(tool) for tool in tools if tool.get("function", {}).get("name") in allowed]


def estimate_payload_chars(tools):
    return sum(len(str(tool)) for tool in tools)
