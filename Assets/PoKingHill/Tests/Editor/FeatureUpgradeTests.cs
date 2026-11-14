using System;
using System.IO;
using NUnit.Framework;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEngine;

namespace PoKingHill.Tests
{
    /// <summary>EditMode unit tests covering the 20 new gameplay, inference, atmosphere, physics, and API features.</summary>
    public class FeatureUpgradeTests
    {
        [Test]
        public void PredictionBookScoresStreakAndPersists()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "koth_pred_test.json");
            File.Delete(tmp);
            PredictionBook.FileOverride = tmp;
            try
            {
                PredictionBook.ResetAll();
                PredictionBook.SetPick("Kim 2", 0.25f);
                string winLine = PredictionBook.Settle("Kim 2");
                Assert.IsTrue(winLine.Contains("HIT"));
                Assert.AreEqual(1, PredictionBook.Picks);
                Assert.AreEqual(1, PredictionBook.Correct);
                Assert.AreEqual(1, PredictionBook.Streak);
                Assert.Greater(PredictionBook.Points, 0);

                PredictionBook.SetPick("Kim 2", 0.50f);
                PredictionBook.Settle("Trump 1");
                Assert.AreEqual(2, PredictionBook.Picks);
                Assert.AreEqual(1, PredictionBook.Correct);
                Assert.AreEqual(0, PredictionBook.Streak);
                Assert.AreEqual(1, PredictionBook.BestStreak);

                // Reload from disk via FileOverride setter
                PredictionBook.FileOverride = tmp;
                Assert.AreEqual(2, PredictionBook.Picks);
                Assert.AreEqual(1, PredictionBook.BestStreak);
            }
            finally { PredictionBook.FileOverride = null; File.Delete(tmp); }
        }

        [Test]
        public void RivalryBookTracksKnockoutsNemesisAndPersists()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "koth_rivalry_test.json");
            File.Delete(tmp);
            RivalryBook.FileOverride = tmp;
            try
            {
                RivalryBook.ResetAll();
                RivalryBook.RecordKnockout("Trump 1", "Kim 1");
                RivalryBook.RecordKnockout("Trump 1", "Kim 1");
                RivalryBook.RecordMatchResult("Trump 1", new[] { "Kim 1", "Trump 1", "Grandpa 1" });
                RivalryBook.RecordMatchResult("Trump 1", new[] { "Kim 1", "Trump 1" });

                var (aWins, bWins, aKosB, bKosA) = RivalryBook.HeadToHead("Trump 1", "Kim 1");
                Assert.AreEqual(2, aWins);
                Assert.AreEqual(0, bWins);
                Assert.AreEqual(2, aKosB);
                Assert.AreEqual(0, bKosA);

                var (nemesis, losses, kos) = RivalryBook.NemesisOf("Kim 1");
                Assert.AreEqual("Trump 1", nemesis);
                Assert.AreEqual(2, losses);
                Assert.AreEqual(2, kos);

                RivalryBook.FileOverride = tmp;
                var career = RivalryBook.CareerOf("Trump 1");
                Assert.AreEqual(2, career.wins);
                Assert.AreEqual(2, career.kos);
                Assert.AreEqual(2, career.matches);
            }
            finally { RivalryBook.FileOverride = null; File.Delete(tmp); }
        }

        [TestCase("Assets/PoKingHill/Models/kim/fighter_policy.onnx", 112)]
        [TestCase("Assets/PoKingHill/Models/trump/fighter_policy.onnx", 112)]
        [TestCase("Assets/PoKingHill/Models/grandpa/fighter_policy.onnx", 112)]
        public void PolicyBatcherMatchesSingleRunnerInference(string assetPath, int obsDim)
        {
            var asset = AssetDatabase.LoadAssetAtPath<ModelAsset>(assetPath);
            Assert.IsNotNull(asset, $"Missing ModelAsset at {assetPath}");
            var rawModel = ModelLoader.Load(asset);

            var rng = new System.Random(12345);
            const int N = 6;
            var batchObs = new float[N][];
            for (int i = 0; i < N; i++)
            {
                batchObs[i] = new float[obsDim];
                for (int j = 0; j < obsDim; j++) batchObs[i][j] = (float)(rng.NextDouble() * 2.0 - 1.0);
            }

            float[][] batched = PolicyBatcher.RunBatchOnce(asset, obsDim, batchObs);
            Assert.AreEqual(N, batched.Length);

            using var singleWorker = new Worker(rawModel, BackendType.CPU);
            using var singleIn = new Tensor<float>(new TensorShape(1, obsDim));
            for (int i = 0; i < N; i++)
            {
                singleIn.Upload(batchObs[i]);
                singleWorker.Schedule(singleIn);
                using var singleOut = (singleWorker.PeekOutput() as Tensor<float>).ReadbackAndClone();
                float[] expected = singleOut.DownloadToArray();
                Assert.AreEqual(expected.Length, batched[i].Length);
                float maxDiff = 0f;
                for (int k = 0; k < expected.Length; k++)
                    maxDiff = Mathf.Max(maxDiff, Mathf.Abs(expected[k] - batched[i][k]));
                Assert.Less(maxDiff, 1e-4f, $"Batch parity failed for {assetPath} row {i}: maxDiff={maxDiff}");
            }
        }

        [Test]
        public void BuildHeatsSchedulePartitionsIntoHeatsAndFinal()
        {
            var h16 = MatchDirector.BuildHeatsSchedule(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 });
            Assert.AreEqual(5, h16.Count);
            Assert.AreEqual("Heat 1", h16[0].Label);
            Assert.AreEqual(4, h16[0].Entrants.Length);
            Assert.AreEqual("Heats Final", h16[4].Label);
            Assert.AreEqual(4, h16[4].Entrants.Length);

            var h10 = MatchDirector.BuildHeatsSchedule(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 });
            Assert.AreEqual(3, h10.Count);
            Assert.AreEqual(4, h10[0].Entrants.Length);
            Assert.AreEqual(4, h10[1].Entrants.Length);
            Assert.AreEqual("Heats Final", h10[2].Label);
        }

        [Test]
        public void DailyChallengeIsDeterministicAndRanksBoard()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "koth_daily_test.json");
            File.Delete(tmp);
            DailyChallenge.FileOverride = tmp;
            try
            {
                DailyChallenge.ResetAll();
                var d1 = new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);
                var d2 = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);
                Assert.AreEqual(DailyChallenge.TodaySeed(d1), DailyChallenge.TodaySeed(d1));
                Assert.AreNotEqual(DailyChallenge.TodaySeed(d1), DailyChallenge.TodaySeed(d2));

                var pool = new[] { 0, 1, 2, 3, 4, 5, 6, 7 };
                CollectionAssert.AreEqual(DailyChallenge.SelectOrder(pool, d1), DailyChallenge.SelectOrder(pool, d1));

                DailyChallenge.SubmitLocal("Alice", "Kim 1", 120, 19.5f, d1);
                DailyChallenge.SubmitLocal("Bob", "Trump 2", 340, 14.2f, d1);
                DailyChallenge.SubmitLocal("Cara", "Grandpa 1", 340, 11.0f, d1);
                var top = DailyChallenge.TopToday(d1, 5);
                Assert.AreEqual(3, top.Count);
                Assert.AreEqual("Cara", top[0].player);
                Assert.AreEqual("Bob", top[1].player);
                Assert.AreEqual("Alice", top[2].player);
            }
            finally { DailyChallenge.FileOverride = null; File.Delete(tmp); }
        }

        [Test]
        public void ExternalApiParsersHandleOpenMeteoWikiAndDailyJson()
        {
            string meteoJson = "{\"current\":{\"cloud_cover\":82,\"wind_speed_10m\":27.5,\"precipitation\":1.2,\"weather_code\":95}}";
            Assert.IsTrue(LiveAtmosphere.TryParseOpenMeteo(meteoJson, out var sample));
            Assert.AreEqual(0.82f, sample.CloudCover01, 0.01f);
            Assert.Greater(sample.Storm01, 0.5f);
            Assert.AreEqual(27.5f, sample.WindKmh, 0.1f);

            string wikiJson = "{\"extract\":\"Jeju Island is a volcanic island off South Korea.\"}";
            Assert.IsTrue(AgentBioService.TryParseWikiExtract(wikiJson, out string extract));
            Assert.IsTrue(extract.Contains("Jeju Island"));
            Assert.AreEqual("KR", AgentBioService.MetaOf("Kim 1").CountryCode);
            Assert.AreEqual("US", AgentBioService.MetaOf("Trump 2").CountryCode);

            string boardJson = "{\"entries\":[{\"date\":\"2026-11-14\",\"player\":\"Zed\",\"champion\":\"Kim\",\"score\":500,\"durationSec\":9.4}]}";
            var list = DailyChallenge.ParseServerJson(boardJson);
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("Zed", list[0].player);
            Assert.AreEqual(500, list[0].score);
        }

        [Test]
        public void AtmosphereAndSpringBoneBehaveCleanly()
        {
            var noon = AtmosphereController.PresetFor(AtmosphereMode.Noon, DateTime.Now);
            var storm = AtmosphereController.PresetFor(AtmosphereMode.Storm, DateTime.Now);
            var night = AtmosphereController.PresetFor(AtmosphereMode.Night, DateTime.Now);
            Assert.Greater(noon.Intensity, night.Intensity);
            Assert.Greater(storm.Storm, 0.8f);
            Assert.Greater(night.Stars, 0.5f);

            var go = new GameObject("SpringBoneTest");
            try
            {
                var sb = go.AddComponent<SpringBone>();
                sb.ResetChain();
                Vector3 d = sb.Step(0.02f, new Vector4(2.5f, 0f, -0.5f, 1.0f));
                Assert.IsFalse(float.IsNaN(d.x) || float.IsNaN(d.y) || float.IsNaN(d.z));
                Assert.Greater(d.magnitude, 0f);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
