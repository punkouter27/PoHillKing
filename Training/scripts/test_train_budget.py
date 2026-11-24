import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch

from train import TrainingTimeLimit, limit_training_time


class TrainingTimeBudgetTests(unittest.TestCase):
    def test_completed_update_is_logged_before_budget_stop(self):
        log = Mock()
        runner = SimpleNamespace(logger=SimpleNamespace(log=log))
        limit_training_time(runner, 100)
        with patch("train.time.monotonic", return_value=100):
            with self.assertRaises(TrainingTimeLimit):
                runner.logger.log(it=7)
        log.assert_called_once_with(it=7)

    def test_update_continues_before_deadline(self):
        log = Mock()
        runner = SimpleNamespace(logger=SimpleNamespace(log=log))
        limit_training_time(runner, 100)
        with patch("train.time.monotonic", return_value=99):
            runner.logger.log(it=6)
        log.assert_called_once_with(it=6)


if __name__ == "__main__":
    unittest.main()
