using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit.Sdk;
using Xunit.v3;

namespace Hangfire.Core.Tests
{
    /// <summary>
    /// Shared logic for <see cref="DataCompatibilityRangeFactAttribute"/> and
    /// <see cref="DataCompatibilityRangeTheoryAttribute"/>: every test case is run once per
    /// allowed <see cref="CompatibilityLevel"/>, with <see cref="GlobalConfiguration.CompatibilityLevel"/>
    /// set to that level while it runs.
    /// </summary>
    internal static class DataCompatibilityRange
    {
        private const string LevelKey = "DataCompatibilityLevel";

        // GlobalConfiguration.CompatibilityLevel is global state, so only one test case may change it at a time.
        // Not Monitor: xUnit v3 may call PostInvoke on a different thread than PreInvoke.
        private static readonly SemaphoreSlim SyncRoot = new SemaphoreSlim(1, 1);
        private static CompatibilityLevel _previousLevel;

        internal static readonly CompatibilityLevel PossibleMinLevel;
        internal static readonly CompatibilityLevel PossibleMaxExcludingLevel;

        static DataCompatibilityRange()
        {
            var levels = GetAllLevels();
            PossibleMinLevel = levels.Min();
            PossibleMaxExcludingLevel = levels.Max() + 1;
        }

        public static IReadOnlyCollection<IXunitTestCase> Expand(
            IReadOnlyCollection<IXunitTestCase> testCases,
            IDataCompatibilityRangeAttribute range)
        {
            var levels = GetAllLevels()
                .Where(level => level >= range.MinLevel && level < range.MaxExcludingLevel)
                .ToArray();

            var result = new List<IXunitTestCase>();

            foreach (var testCase in testCases)
            {
                // Check the derived type first, it's also an XunitTestCase
                if (testCase is XunitDelayEnumeratedTheoryTestCase theoryTestCase)
                {
                    result.AddRange(levels.Select(level => new DataCompatibilityRangeTheoryTestCase(level, theoryTestCase)));
                }
                else if (testCase.GetType() == typeof(XunitTestCase))
                {
                    result.AddRange(levels.Select(level => new DataCompatibilityRangeTestCase(level, (XunitTestCase)testCase)));
                }
                else
                {
                    // Error test cases (e.g. a [Fact] with parameters) are reported once, as they are
                    result.Add(testCase);
                }
            }

            return result;
        }

        public static string GetDisplayName(string displayName, CompatibilityLevel level)
        {
            return $"{displayName} [{level}]";
        }

        public static string GetUniqueId(string uniqueId, CompatibilityLevel level)
        {
            return UniqueIDGenerator.ForTestCase(uniqueId, (int)level);
        }

        public static void Serialize(IXunitSerializationInfo info, CompatibilityLevel level)
        {
            info.AddValue(LevelKey, level.ToString());
        }

        public static CompatibilityLevel Deserialize(IXunitSerializationInfo info)
        {
            return (CompatibilityLevel)Enum.Parse(typeof(CompatibilityLevel), info.GetValue<string>(LevelKey));
        }

        public static void Enter(CompatibilityLevel level)
        {
            SyncRoot.Wait();
            _previousLevel = GlobalConfiguration.CompatibilityLevel;
            GlobalConfiguration.CompatibilityLevel = level;
        }

        public static void Exit()
        {
            GlobalConfiguration.CompatibilityLevel = _previousLevel;
            SyncRoot.Release();
        }

        private static CompatibilityLevel[] GetAllLevels()
        {
            return Enum.GetValues(typeof(CompatibilityLevel)).Cast<CompatibilityLevel>().ToArray();
        }
    }

    internal interface IDataCompatibilityRangeAttribute
    {
        CompatibilityLevel MinLevel { get; }
        CompatibilityLevel MaxExcludingLevel { get; }
    }
}
