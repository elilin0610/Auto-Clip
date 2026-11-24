import math
import unittest

from autoclip import parse_scene_changes, select_highlight_clips


class ParseSceneChangesTests(unittest.TestCase):
    def test_reads_timestamp_and_scene_score_from_ffmpeg_metadata(self):
        output = (
            "frame:0 pts:3000 pts_time:3.000\n"
            "lavfi.scene_score=0.412\n"
            "frame:1 pts:9000 pts_time:9.000\n"
            "lavfi.scene_score=0.875\n"
        )

        self.assertEqual(
            parse_scene_changes(output), [(3.0, 0.412), (9.0, 0.875)]
        )

    def test_ignores_incomplete_metadata(self):
        output = "frame:0 pts:0 pts_time:0.000\nframe:1 pts:3 pts_time:1.000\n"

        self.assertEqual(parse_scene_changes(output), [])


class SelectHighlightClipsTests(unittest.TestCase):
    def test_prefers_high_scoring_scene_changes_without_overlaps(self):
        changes = [(4, 0.2), (30, 0.95), (33, 0.8), (50, 0.7)]

        clips = select_highlight_clips(changes, 60, 10, 3)

        self.assertEqual(
            [(clip.start, clip.end, clip.score) for clip in clips],
            [(0, 10, 0.2), (25, 35, 0.95), (45, 55, 0.7)],
        )

    def test_fills_missing_scenes_with_evenly_spaced_clips(self):
        clips = select_highlight_clips([], 60, 12, 3)

        self.assertEqual(len(clips), 3)
        self.assertTrue(all(clip.score == 0 for clip in clips))
        self.assertTrue(
            all(left.end <= right.start for left, right in zip(clips, clips[1:]))
        )

    def test_scene_clip_does_not_prevent_filling_available_capacity(self):
        clips = select_highlight_clips([(5, 1.0)], 10, 4, 2)

        self.assertEqual(len(clips), 2)
        self.assertTrue(all(clip.score == 0 for clip in clips))
        self.assertTrue(clips[0].end <= clips[1].start)

    def test_short_video_produces_one_clip_covering_the_video(self):
        clips = select_highlight_clips([], 8, 12, 3)

        self.assertEqual(len(clips), 1)
        self.assertEqual((clips[0].start, clips[0].end), (0, 8))

    def test_discards_out_of_range_and_non_finite_scenes(self):
        clips = select_highlight_clips(
            [(-1, 5), (float("nan"), 1), (12, float("inf"))], 10, 5, 1
        )

        self.assertEqual(len(clips), 1)
        self.assertEqual(clips[0].score, 0)

    def test_rejects_non_positive_duration_or_count(self):
        for duration, clip_duration, count in (
            (0, 5, 1),
            (10, 0, 1),
            (10, 5, 0),
            (math.inf, 5, 1),
        ):
            with self.subTest(duration=duration, clip_duration=clip_duration, count=count):
                with self.assertRaises(ValueError):
                    select_highlight_clips([], duration, clip_duration, count)


if __name__ == "__main__":
    unittest.main()
