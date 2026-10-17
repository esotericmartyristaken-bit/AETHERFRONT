using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Client;
using System.Linq;
using System.Threading.Tasks;

namespace ServerTest
{
    [TestClass]
    public class StartupReplyGateTest
    {
        [TestMethod]
        public void CompletedReplySuppressesRetries()
        {
            var gate = new StartupReplyGate();

            Assert.IsTrue(gate.TryBegin(StartupReplyKind.Flags));
            gate.Complete(StartupReplyKind.Flags);

            Assert.IsFalse(gate.TryBegin(StartupReplyKind.Flags));
            Assert.IsTrue(gate.IsQueued(StartupReplyKind.Flags));
            Assert.AreEqual(1, gate.SuppressionCount(StartupReplyKind.Flags));
        }

        [TestMethod]
        public void InProgressReplySuppressesConcurrentRetry()
        {
            var gate = new StartupReplyGate();

            Assert.IsTrue(gate.TryBegin(StartupReplyKind.Flags));
            Assert.IsFalse(gate.TryBegin(StartupReplyKind.Flags));

            gate.Complete(StartupReplyKind.Flags);
            Assert.IsTrue(gate.IsQueued(StartupReplyKind.Flags));
        }

        [TestMethod]
        public void FailedQueueAllowsRetry()
        {
            var gate = new StartupReplyGate();

            Assert.IsTrue(gate.TryBegin(StartupReplyKind.Flags));
            gate.Abort(StartupReplyKind.Flags);
            Assert.IsTrue(gate.TryBegin(StartupReplyKind.Flags));
        }

        [TestMethod]
        public void ReplyKindsAreIndependent()
        {
            var gate = new StartupReplyGate();

            Assert.IsTrue(gate.TryBegin(StartupReplyKind.Flags));
            gate.Complete(StartupReplyKind.Flags);

            Assert.IsTrue(gate.TryBegin(StartupReplyKind.Players));
            Assert.IsTrue(gate.TryBegin(StartupReplyKind.Scenarios));
        }

        [TestMethod]
        public void ConcurrentRequestsHaveExactlyOneWinner()
        {
            var gate = new StartupReplyGate();
            var results = Enumerable.Range(0, 64)
                .AsParallel()
                .Select(_ => gate.TryBegin(StartupReplyKind.Flags))
                .ToArray();

            Assert.AreEqual(1, results.Count(result => result));
            Assert.AreEqual(63, gate.SuppressionCount(StartupReplyKind.Flags));
        }
    }
}
