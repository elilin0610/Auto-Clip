# Auto-Clip

Auto-Clip is a lightweight desktop app that finds visually active moments in a
video and exports them as short MP4 clips. It scores FFmpeg scene changes,
selects the strongest non-overlapping moments, and spaces clips evenly if the
video has too few detected scene changes.

## Requirements

- Python 3.10 or later with Tkinter
- FFmpeg and FFprobe available on your `PATH`

## Run

```sh
python3 autoclip.py
```

Choose a video, set the desired clip length and number of clips, and select
**分析影片**. The app lists the chosen time ranges; select clips and choose
**匯出所選片段** to save them in the output folder. Existing output files are
preserved by giving new clips a numbered suffix.

## Test

```sh
python3 -m unittest discover -s tests
```
