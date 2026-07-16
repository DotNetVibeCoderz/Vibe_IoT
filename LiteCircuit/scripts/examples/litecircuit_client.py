"""LiteCircuit REST API example: create a board from a template, auto-route it,
run DRC, and download the fabrication files.

Usage:
    pip install requests
    python litecircuit_client.py [base_url]
"""
import sys
import requests

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5210") + "/api"


def main() -> None:
    # 1. Discover templates
    templates = requests.get(f"{BASE}/templates").json()
    print("Templates:", ", ".join(t["key"] for t in templates))

    # 2. Create a project from the plant-monitor template
    project = requests.post(f"{BASE}/projects", json={
        "name": "Sensor node (scripted)",
        "description": "Created by litecircuit_client.py",
        "templateKey": "plant-monitor",
    }).json()
    pid = project["id"]
    print("Created project", pid)

    # 3. Auto-route
    route = requests.post(f"{BASE}/projects/{pid}/autoroute").json()
    print("Router:", route["report"])

    # 4. DRC
    drc = requests.get(f"{BASE}/projects/{pid}/drc").json()
    print(f"DRC: {len(drc)} finding(s)")
    for v in drc[:5]:
        print(f"  [{v['severity']}] {v['rule']}: {v['message']}")

    # 5. Component search: cheapest in-stock MCUs
    mcus = requests.get(f"{BASE}/components",
                        params={"category": "MCU", "minStock": 1000}).json()
    for c in mcus[:3]:
        print(f"  MCU option: {c['name']} — ${c['priceUsd']} ({c['stock']} in stock)")

    # 6. Download outputs
    for suffix, out in [("gerber.zip", "output-gerber.zip"),
                        ("bom.csv", "output-bom.csv"),
                        ("board.stl", "output-board.stl")]:
        data = requests.get(f"{BASE}/projects/{pid}/{suffix}").content
        with open(out, "wb") as f:
            f.write(data)
        print(f"Saved {out} ({len(data)} bytes)")


if __name__ == "__main__":
    main()
