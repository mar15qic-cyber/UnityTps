import unittest
from summarize_evidence import summarize


class EvidenceTests(unittest.TestCase):
    def test_empty_evidence_cannot_pass(self):
        result = summarize([])
        self.assertEqual(result["experience"], "unverified")
        self.assertEqual(result["sessions"], [])

    def test_respawns_are_not_abnormal_and_short_session_does_not_pass(self):
        result = summarize([{"kind":"rebase", "reason":"DeathRespawn", "time":0},
                            {"kind":"rebase", "reason":"Snap", "time":60}])["sessions"][0]
        self.assertEqual(result["abnormalRebases"], 1)
        self.assertEqual(result["abnormalRebasesPer10Minutes"], 10)
        self.assertEqual(result["movementMetricGate"], "insufficient-evidence")

    def test_metrics_never_certify_real_world_test(self):
        rows = [{"kind":"reconcile", "error":.01, "time":i*6} for i in range(101)]
        result = summarize(rows)
        self.assertEqual(result["sessions"][0]["movementMetricGate"], "pass")
        self.assertEqual(result["experience"], "unverified")


if __name__ == "__main__":
    unittest.main()
