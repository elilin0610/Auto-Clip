#!/usr/bin/env python3
"""Desktop application for finding and exporting video highlight clips."""

from __future__ import annotations

import math
import re
import subprocess
import threading
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Clip:
    start: float
    end: float
    score: float


_SCENE_FRAME = re.compile(
    r"pts_time:([+-]?\d+(?:\.\d+)?).*?lavfi\.scene_score=([+-]?\d+(?:\.\d+)?)",
    re.DOTALL,
)


def parse_scene_changes(output: str) -> list[tuple[float, float]]:
    """Extract (timestamp, scene score) pairs from FFmpeg metadata output."""
    changes = []
    for frame in re.split(r"(?=frame:\s*\d+)", output):
        match = _SCENE_FRAME.search(frame)
        if match:
            timestamp, score = map(float, match.groups())
            if math.isfinite(timestamp) and math.isfinite(score):
                changes.append((timestamp, score))
    return changes


def select_highlight_clips(
    scene_changes: list[tuple[float, float]],
    video_duration: float,
    clip_duration: float,
    clip_count: int,
) -> list[Clip]:
    """Choose strong, non-overlapping scene changes, filling gaps evenly."""
    if not math.isfinite(video_duration) or video_duration <= 0:
        raise ValueError("Video duration must be greater than zero.")
    if not math.isfinite(clip_duration) or clip_duration <= 0:
        raise ValueError("Clip duration must be greater than zero.")
    if clip_count < 1:
        raise ValueError("Clip count must be at least one.")

    length = min(clip_duration, video_duration)
    count = min(clip_count, max(1, int(video_duration / length)))
    latest_start = video_duration - length

    def make_clip(center: float, score: float) -> Clip:
        start = min(max(center - length / 2, 0), latest_start)
        return Clip(start=start, end=start + length, score=score)

    selected: list[Clip] = []

    def remaining_capacity(clips: list[Clip]) -> int:
        capacity = len(clips)
        cursor = 0.0
        for item in sorted(clips, key=lambda clip: clip.start):
            gap = max(0.0, item.start - cursor)
            capacity += int((gap + 1e-9) / length)
            cursor = item.end
        capacity += int((max(0.0, video_duration - cursor) + 1e-9) / length)
        return capacity

    candidates = sorted(
        (
            (timestamp, score)
            for timestamp, score in scene_changes
            if math.isfinite(timestamp)
            and math.isfinite(score)
            and 0 <= timestamp <= video_duration
        ),
        key=lambda item: item[1],
        reverse=True,
    )
    for timestamp, score in candidates:
        clip = make_clip(timestamp, score)
        proposed = selected + [clip]
        if (
            all(
                clip.end <= item.start or clip.start >= item.end
                for item in selected
            )
            and remaining_capacity(proposed) >= count
        ):
            selected.append(clip)
            if len(selected) == count:
                break

    occupied = sorted(selected, key=lambda item: item.start)
    gaps = []
    cursor = 0.0
    for item in occupied:
        gaps.append((cursor, item.start))
        cursor = item.end
    gaps.append((cursor, video_duration))
    for gap_start, gap_end in gaps:
        available = max(0.0, gap_end - gap_start)
        slots = min(
            count - len(selected),
            int((available + 1e-9) / length),
        )
        if slots <= 0:
            continue
        spacing = max(0.0, available - slots * length) / (slots + 1)
        for index in range(slots):
            start = gap_start + spacing * (index + 1) + index * length
            selected.append(Clip(start, start + length, 0.0))
        if len(selected) == count:
            break

    return sorted(selected, key=lambda clip: clip.start)


def _run(command: list[str]) -> subprocess.CompletedProcess[str]:
    try:
        return subprocess.run(command, check=True, capture_output=True, text=True)
    except FileNotFoundError as error:
        raise RuntimeError(
            "FFmpeg and FFprobe are required. Install FFmpeg and ensure both "
            "commands are available on PATH."
        ) from error
    except subprocess.CalledProcessError as error:
        details = (error.stderr or error.stdout or "").strip()
        raise RuntimeError(details or "The media command failed.") from error


def probe_video_duration(video_path: str | Path) -> float:
    result = _run(
        [
            "ffprobe",
            "-v",
            "error",
            "-show_entries",
            "format=duration",
            "-of",
            "default=noprint_wrappers=1:nokey=1",
            str(video_path),
        ]
    )
    try:
        duration = float(result.stdout.strip())
    except ValueError as error:
        raise RuntimeError("Could not read the video's duration.") from error
    if not math.isfinite(duration) or duration <= 0:
        raise RuntimeError("The selected video has no valid duration.")
    return duration


def analyze_video(
    video_path: str | Path, clip_duration: float, clip_count: int
) -> list[Clip]:
    duration = probe_video_duration(video_path)
    result = _run(
        [
            "ffmpeg",
            "-hide_banner",
            "-i",
            str(video_path),
            "-vf",
            "select='gt(scene,0.08)',metadata=print:file=-",
            "-an",
            "-f",
            "null",
            "-",
        ]
    )
    scene_changes = parse_scene_changes(result.stdout + "\n" + result.stderr)
    return select_highlight_clips(
        scene_changes, duration, clip_duration, clip_count
    )


def _unique_output_path(directory: Path, index: int) -> Path:
    candidate = directory / f"highlight_{index:02d}.mp4"
    suffix = 1
    while candidate.exists():
        candidate = directory / f"highlight_{index:02d}_{suffix}.mp4"
        suffix += 1
    return candidate


def export_clips(
    video_path: str | Path, output_directory: str | Path, clips: list[Clip]
) -> list[Path]:
    directory = Path(output_directory)
    directory.mkdir(parents=True, exist_ok=True)
    outputs = []
    for index, clip in enumerate(clips, start=1):
        output_path = _unique_output_path(directory, index)
        _run(
            [
                "ffmpeg",
                "-hide_banner",
                "-nostdin",
                "-ss",
                f"{clip.start:.3f}",
                "-i",
                str(video_path),
                "-t",
                f"{clip.end - clip.start:.3f}",
                "-map",
                "0:v:0",
                "-map",
                "0:a?",
                "-c:v",
                "libx264",
                "-preset",
                "veryfast",
                "-crf",
                "21",
                "-c:a",
                "aac",
                "-movflags",
                "+faststart",
                str(output_path),
            ]
        )
        outputs.append(output_path)
    return outputs


def run_app() -> None:
    try:
        import tkinter as tk
        from tkinter import filedialog, messagebox, ttk
    except ImportError as error:
        raise RuntimeError(
            "Tkinter is required to run Auto-Clip. Install your Python "
            "distribution's Tkinter package and try again."
        ) from error

    class AutoClipApp:
        def __init__(self, root: tk.Tk) -> None:
            self.root = root
            self.root.title("Auto-Clip — 精華剪輯")
            self.root.geometry("700x540")
            self.root.minsize(580, 440)
            self.video_path = tk.StringVar()
            self.output_path = tk.StringVar()
            self.clip_duration = tk.StringVar(value="12")
            self.clip_count = tk.StringVar(value="3")
            self.status = tk.StringVar(value="請選擇影片以開始使用。")
            self.clips: list[Clip] = []

            main = ttk.Frame(root, padding=22)
            main.pack(fill="both", expand=True)
            ttk.Label(
                main, text="Auto-Clip 自動精華剪輯", font=("TkDefaultFont", 22, "bold")
            ).pack(anchor="w")
            ttk.Label(
                main,
                text="分析影片中的場景變化，自動挑選片段並匯出。",
            ).pack(anchor="w", pady=(2, 18))

            self._path_row(main, "影片", self.video_path, self._choose_video)
            self._path_row(main, "輸出資料夾", self.output_path, self._choose_output)

            settings = ttk.Frame(main)
            settings.pack(fill="x", pady=(12, 8))
            ttk.Label(settings, text="片段長度（秒）").pack(side="left")
            ttk.Spinbox(
                settings, from_=1, to=300, width=6, textvariable=self.clip_duration
            ).pack(side="left", padx=(8, 24))
            ttk.Label(settings, text="片段數").pack(side="left")
            ttk.Spinbox(
                settings, from_=1, to=10, width=5, textvariable=self.clip_count
            ).pack(side="left", padx=8)
            self.analyze_button = ttk.Button(
                settings, text="分析影片", command=self._analyze
            )
            self.analyze_button.pack(side="right")

            ttk.Label(main, text="偵測到的精華片段").pack(anchor="w", pady=(12, 5))
            list_frame = ttk.Frame(main)
            list_frame.pack(fill="both", expand=True)
            self.clip_list = tk.Listbox(
                list_frame, selectmode=tk.EXTENDED, height=10, activestyle="dotbox"
            )
            scrollbar = ttk.Scrollbar(
                list_frame, orient="vertical", command=self.clip_list.yview
            )
            self.clip_list.configure(yscrollcommand=scrollbar.set)
            self.clip_list.pack(side="left", fill="both", expand=True)
            scrollbar.pack(side="right", fill="y")

            footer = ttk.Frame(main)
            footer.pack(fill="x", pady=(12, 0))
            ttk.Label(footer, textvariable=self.status).pack(side="left", fill="x", expand=True)
            self.export_button = ttk.Button(
                footer, text="匯出所選片段", command=self._export
            )
            self.export_button.pack(side="right")
            self.export_button.state(["disabled"])

        @staticmethod
        def _path_row(
            parent: ttk.Frame,
            label: str,
            variable: tk.StringVar,
            browse_command,
        ) -> None:
            row = ttk.Frame(parent)
            row.pack(fill="x", pady=5)
            ttk.Label(row, text=label, width=14).pack(side="left")
            ttk.Entry(row, textvariable=variable).pack(
                side="left", fill="x", expand=True, padx=(0, 8)
            )
            ttk.Button(row, text="瀏覽…", command=browse_command).pack(side="right")

        def _choose_video(self) -> None:
            path = filedialog.askopenfilename(
                title="選擇影片",
                filetypes=[
                    ("Video files", "*.mp4 *.mov *.mkv *.avi *.webm *.m4v"),
                    ("All files", "*"),
                ],
            )
            if path:
                self.video_path.set(path)
                self.output_path.set(str(Path(path).parent / "highlights"))
                self.clip_list.delete(0, tk.END)
                self.clips.clear()
                self.export_button.state(["disabled"])

        def _choose_output(self) -> None:
            path = filedialog.askdirectory(title="選擇輸出資料夾")
            if path:
                self.output_path.set(path)

        def _set_busy(self, busy: bool, status: str) -> None:
            self.status.set(status)
            state = ["disabled"] if busy else ["!disabled"]
            self.analyze_button.state(state)

        def _analyze(self) -> None:
            source = self.video_path.get().strip()
            if not source or not Path(source).is_file():
                messagebox.showerror("選擇影片", "請選擇一個存在的影片檔案。")
                return
            try:
                duration = int(self.clip_duration.get())
                count = int(self.clip_count.get())
                if duration < 1 or count < 1:
                    raise ValueError
            except ValueError:
                messagebox.showerror(
                    "設定錯誤",
                    "片段長度和片段數必須是正整數。",
                )
                return

            self.clips.clear()
            self.clip_list.delete(0, tk.END)
            self.export_button.state(["disabled"])
            self._set_busy(True, "正在分析場景變化…")

            def work() -> None:
                try:
                    clips = analyze_video(source, duration, count)
                    self.root.after(
                        0, lambda: self._analysis_complete(source, clips)
                    )
                except Exception as error:
                    self.root.after(
                        0,
                        lambda message=str(error): self._analysis_failed(
                            source, message
                        ),
                    )

            threading.Thread(target=work, daemon=True).start()

        def _analysis_complete(self, source: str, clips: list[Clip]) -> None:
            if source != self.video_path.get().strip():
                self._set_busy(False, "影片已變更，請重新分析。")
                return
            self.clips = clips
            for index, clip in enumerate(clips, start=1):
                self.clip_list.insert(
                    tk.END,
                    f"{index:02d}  {self._time(clip.start)}–{self._time(clip.end)}"
                    f"   場景分數 {clip.score:.3f}",
                )
            if clips:
                self.clip_list.selection_set(0, tk.END)
                self.export_button.state(["!disabled"])
                self._set_busy(False, f"找到 {len(clips)} 個片段，可選取後匯出。")
            else:
                self._set_busy(False, "找不到可用的精華片段。")

        def _analysis_failed(self, source: str, error: str) -> None:
            self._set_busy(False, "分析失敗。")
            if source == self.video_path.get().strip():
                messagebox.showerror("無法分析影片", error)

        def _export(self) -> None:
            source = self.video_path.get().strip()
            output = self.output_path.get().strip()
            if not output:
                messagebox.showerror("選擇輸出資料夾", "請選擇輸出資料夾。")
                return
            selected = self.clip_list.curselection()
            if not selected:
                messagebox.showerror("選擇片段", "請至少選擇一個片段匯出。")
                return
            clips = [self.clips[index] for index in selected]
            self.export_button.state(["disabled"])
            self.analyze_button.state(["disabled"])
            self.status.set("正在匯出 MP4 片段…")

            def work() -> None:
                try:
                    paths = export_clips(source, output, clips)
                    self.root.after(0, lambda: self._export_complete(paths))
                except Exception as error:
                    self.root.after(
                        0, lambda message=str(error): self._export_failed(message)
                    )

            threading.Thread(target=work, daemon=True).start()

        def _export_complete(self, paths: list[Path]) -> None:
            self.analyze_button.state(["!disabled"])
            self.export_button.state(["!disabled"])
            self.status.set(f"已匯出 {len(paths)} 個片段至 {paths[0].parent}。")
            messagebox.showinfo("匯出完成", f"已儲存 {len(paths)} 個片段。")

        def _export_failed(self, error: str) -> None:
            self.analyze_button.state(["!disabled"])
            self.export_button.state(["!disabled"])
            self.status.set("匯出失敗。")
            messagebox.showerror("無法匯出片段", error)

        @staticmethod
        def _time(seconds: float) -> str:
            minutes, seconds = divmod(int(seconds), 60)
            return f"{minutes:02d}:{seconds:02d}"

    root = tk.Tk()
    AutoClipApp(root)
    root.mainloop()


if __name__ == "__main__":
    try:
        run_app()
    except RuntimeError as error:
        raise SystemExit(str(error)) from error
