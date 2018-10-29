﻿using Phantasma.Cryptography;

namespace Phantasma.Blockchain.Contracts.Native
{
    public sealed class ConsensusContract : NativeContract
    {
        public override ContractKind Kind => ContractKind.Custom;

        public ConsensusContract() : base()
        {
        }

        public bool IsValidMiner(Address address)
        {
            return true;
        }

        public bool IsValidReceiver(Address address)
        {
            return true;
        }
    }
}
