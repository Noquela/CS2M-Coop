using System.Collections.Generic;
using System.Linq;
using CS2M.Sync;
using NUnit.Framework;

namespace CS2M.Test
{
    /// <summary>
    ///     Thin CI wrapper over <see cref="SyncContract.Verify"/> — the real logic and manifest live in the
    ///     mod assembly (so the in-game selftest runs the SAME check). This fixture only runs where the game
    ///     DLLs resolve; headless (no game refs) NUnit cannot load the merged CS2M.dll, which is why the
    ///     authoritative run of this guarantee is the in-game selftest. See <see cref="SyncContract"/>.
    /// </summary>
    [TestFixture]
    public class SyncCompletenessTest
    {
        [Test]
        public void SyncContractIsComplete()
        {
            List<string> violations = SyncContract.Verify();
            Assert.That(violations, Is.Empty,
                "Contrato de sync incompleto — nada pode ficar de fora:\n  " + string.Join("\n  ", violations));
        }
    }
}
