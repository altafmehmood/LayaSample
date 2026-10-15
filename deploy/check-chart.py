#!/usr/bin/env python3
"""Checks what the chart renders for the internal-transport-security plan (Plans/0001). Needs only helm and the standard library.

    python3 deploy/check-chart.py        # from the repository root

Each check names the rule it protects. It reads the rendered text, so it does not need a cluster or the Dapr and cert-manager CRDs.
"""
import re
import subprocess
import sys

CHART = "deploy/helm/docintake"
failures = []


def render(*args):
    return subprocess.run(["helm", "template", "t", CHART, "-f", "deploy/values-prod.yaml", *args],
                          check=True, capture_output=True, text=True).stdout


def documents(text):
    return [d for d in re.split(r"^---\s*$", text, flags=re.M) if d.strip()]


def of_kind(docs, kind):
    return [d for d in docs if re.search(rf"^kind: {kind}\s*$", d, re.M)]


def named(docs, name):
    return [d for d in docs if re.search(rf"^  name: {re.escape(name)}\s*$", d, re.M)]


def check(ok, message):
    if not ok:
        failures.append(message)
        print(f"FAIL  {message}")
    else:
        print(f"ok    {message}")


def ingress_ports(policy):
    section = policy.split("ingress:", 1)[1].split("egress:", 1)[0]
    return re.findall(r"port: (\d+)", section)


secured = render()
docs = documents(secured)

# --- Dapr: the API reaches the services through its sidecar, and only the API is let in ---------------------------------------
check(not re.search(r"http://t-docintake-(document|extraction|clamav):8080", secured),
      "no call to a service uses its plain HTTP Service address")
check(len(re.findall(r"localhost:3500/v1\.0/invoke/t-docintake-(document|extraction|scan)/method", secured)) == 3,
      "the API calls document, extraction and scan through its Dapr sidecar")
for service in ("document", "extraction", "clamav"):
    deployment = named(of_kind(docs, "Deployment"), f"t-docintake-{service}")
    check(len(deployment) == 1 and "dapr.io/enabled: \"true\"" in deployment[0] and "dapr.io/config:" in deployment[0],
          f"the {service} pod has a Dapr sidecar and an access policy (dapr.io/config)")
configurations = of_kind(docs, "Configuration")
check(len(configurations) == 4, "there is one Dapr Configuration per service (document, extraction, scan, api)")
for configuration in configurations:
    name = re.search(r"^  name: (\S+)", configuration, re.M).group(1)
    check("defaultAction: deny" in configuration and "enabled: true" in configuration.split("accessControl")[0],
          f"{name}: mutual TLS is on and the default is deny")
    if not name.endswith("-api"):
        check("appId: t-docintake-api" in configuration and configuration.count("appId:") == 1,
              f"{name}: only the API's app id is allowed")

# --- NetworkPolicy: the callees are reachable on Dapr's port only ---------------------------------------------------------------
policies = of_kind(docs, "NetworkPolicy")
for service in ("document", "extraction", "clamav"):
    policy = named(policies, f"t-docintake-{service}")
    check(len(policy) == 1 and ingress_ports(policy[0]) == ["50002"],
          f"the {service} NetworkPolicy lets the API in on Dapr's port 50002 only (not 8080, and never clamd's 3310)")
check("3310" not in "".join(named(of_kind(docs, "Service"), "t-docintake-clamav")),
      "the ClamAV Service does not publish clamd's port 3310")

# --- TLS to the API and to the collector -------------------------------------------------------------------------------------------
api = named(of_kind(docs, "Deployment"), "t-docintake-api")[0]
check("containerPort: 8443" in api and "containerPort: 8080" not in api and "Tls__CertificatePath" in api,
      "the API listens on HTTPS 8443 only")
check(all(("scheme: HTTPS" in line) for line in api.splitlines() if "path: /health/" in line),
      "the API's probes use HTTPS")
check(len(of_kind(docs, "BackendTLSPolicy")) == 1, "the Gateway checks the API's certificate (BackendTLSPolicy)")
check(re.search(r"kind: HTTPRoute[\s\S]*?port: 8443", secured) is not None, "the HTTPRoute sends traffic to the API's 8443")
check("https://t-docintake-otel-collector:4318" in secured and "http://t-docintake-otel-collector" not in secured,
      "services send telemetry to the collector over HTTPS")
check(secured.count("OTEL_EXPORTER_OTLP_CERTIFICATE") == 4, "each exporting service trusts the private CA for the collector")
check("cert_file: /tls/tls.crt" in secured, "the collector's receiver terminates TLS")

# --- the switches really switch it off (docker-compose style, and clusters without Dapr or cert-manager) --------------------------
plain = render("--set", "dapr.enabled=false", "--set", "internalTls.enabled=false")
check("dapr.io" not in plain and "kind: Configuration" not in plain, "dapr.enabled=false renders nothing of Dapr")
check("kind: Certificate" not in plain and "BackendTLSPolicy" not in plain and "Tls__CertificatePath" not in plain,
      "internalTls.enabled=false renders no certificates and the API serves HTTP")
check(re.search(r"http://t-docintake-document:8080", plain) is not None, "without Dapr the API calls the Service address")

sys.exit(1 if failures else 0)
