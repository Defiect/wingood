"""Render the saved six-app measurements in the style of the original chart.

Run from the repository: uv run --no-project --with matplotlib python docs/render_live_timeline.py
"""

import json
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import Patch

ROOT = Path(__file__).resolve().parents[1]
INPUT = ROOT / "artifacts/analysis/larger-live-batch-derived.json"
OUTPUT = ROOT / "docs/images/larger-live-timeline.png"


def draw_chart(data: dict) -> None:
    def install_start(package: dict) -> float:
        return min(
            interval["startSeconds"]
            for interval in package["apiIntervals"]
            if interval["state"] == "Installing"
        )

    packages = sorted(data["packages"], key=install_start)
    background = "#f8faff"
    blue, green, gray, red = "#287bb7", "#24856c", "#d9dfe8", "#c94843"
    fig, ax = plt.subplots(figsize=(16, 8), dpi=150)
    fig.patch.set_facecolor(background)
    ax.set_facecolor(background)

    def bar(start: float, end: float, y: int, color: str, height: float = 0.48):
        ax.barh(y, end - start, left=start, height=height, color=color, zorder=3)

    def duration(start: float, end: float, y: int):
        if end - start >= 1.8:
            ax.text(
                (start + end) / 2, y, f"{end - start:.1f}s",
                ha="center", va="center", color="white", weight="bold", fontsize=12,
            )

    labels = []
    for y, package in enumerate(packages):
        start = install_start(package)
        end = max(interval["endSeconds"] for interval in package["apiIntervals"])
        transfer = package["loggedDeliveryOptimizationTransfer"]
        if transfer:
            bar(transfer["endSeconds"], start, y, gray, height=0.16)
            bar(transfer["startSeconds"], transfer["endSeconds"], y, blue)
            duration(transfer["startSeconds"], transfer["endSeconds"], y)
        else:
            ax.text(
                start - 0.35, y, "Download not timed separately",
                ha="right", va="center", fontsize=10, color="#63748a",
            )
        succeeded = package["result"]["succeeded"]
        bar(start, end, y, green if succeeded else red)
        duration(start, end, y)
        name = package["name"]
        if name in {"doppler", "topgrade"}:
            name = name.capitalize()
        labels.append(name if succeeded else f"{name} · failed")

    ax.set_yticks(range(len(packages)), labels)
    ax.set_ylim(len(packages) - 0.35, -0.65)
    ax.set_xlim(0, 33.5)
    ax.set_xticks(range(0, 31, 5))
    ax.set_xlabel("Seconds since batch started", fontsize=14, labelpad=8)
    ax.tick_params(axis="y", length=0, labelsize=14, pad=14)
    ax.tick_params(axis="x", labelsize=13)
    ax.grid(axis="x", color="#dee4ed", linewidth=0.9, zorder=0)
    ax.axvline(data["batchDurationSeconds"], color="#718298", linestyle=":", linewidth=1.2)
    for spine in ("top", "right", "left"):
        ax.spines[spine].set_visible(False)
    ax.spines["bottom"].set_color("#b4c1d1")

    fig.text(
        0.03, 0.94,
        f"Six-app update test: {data['batchDurationSeconds']:.1f} seconds · 5 updated, 1 failed",
        fontsize=23, weight="bold", color="#19293b",
    )
    fig.text(
        0.03, 0.891, "Downloads run together while installations take turns.",
        fontsize=16, color="#3b516d",
    )
    fig.legend(
        handles=[
            Patch(facecolor=blue, label="Downloading"),
            Patch(facecolor=gray, label="Downloaded; waiting"),
            Patch(facecolor=green, label="Installing"),
            Patch(facecolor=red, label="Failed"),
        ],
        loc="upper center", bbox_to_anchor=(0.53, 0.847), ncol=4,
        frameon=False, fontsize=13, columnspacing=2.0, handlelength=2.0,
    )
    fig.text(
        0.03, 0.078,
        "Doppler then updated successfully in a separate 1.5-second retry from a normal terminal.",
        fontsize=12, color="#3b516d",
    )
    fig.text(
        0.03, 0.041,
        "9 Oct 2026 · Install times include Windows preparing, waiting and finishing up. Time shown excludes initial app discovery.",
        fontsize=10.5, color="#63748a",
    )
    fig.subplots_adjust(left=0.165, right=0.97, top=0.75, bottom=0.20)
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(OUTPUT, facecolor=background)
    plt.close(fig)


if __name__ == "__main__":
    draw_chart(json.loads(INPUT.read_text(encoding="utf-8")))
    print(OUTPUT)
