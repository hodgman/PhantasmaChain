﻿using Phantasma.Blockchain.Tokens;
using Phantasma.Cryptography;
using Phantasma.Numerics;
using Phantasma.Storage.Context;

namespace Phantasma.Blockchain.Contracts.Native
{
    public struct GasEventData
    {
        public Address address;
        public BigInteger price;
        public BigInteger amount;
    }

    public struct GasLoanEntry
    {
        public Hash hash;
        public Address borrower;
        public Address lender;
        public BigInteger amount;
    }

    public class GasContract : SmartContract
    {
        public override string Name => "gas";

        internal StorageMap _allowanceMap; //<Address, BigInteger>
        internal StorageMap _allowanceTargets; //<Address, Address>

        internal StorageMap _loanMap; // Address, GasLendEntry
        internal StorageMap _loanList; // Address, List<GasLendEntry>
        internal StorageMap _lenderMap; // Address, Address
        internal StorageList _lenderList; // Address

        public const int MaxLendAmount = 9999;
        public const int LendReturn = 50;
        public const int MaxLenderCount = 10;

        public void AllowGas(Address user, Address target, BigInteger price, BigInteger limit)
        {
            if (Runtime.readOnlyMode)
            {
                return;
            }

            Runtime.Expect(user.IsUser, "must be a user address");
            Runtime.Expect(IsWitness(user), "invalid witness");
            Runtime.Expect(target.IsSystem, "destination must be system address");

            Runtime.Expect(price > 0, "price must be positive amount");
            Runtime.Expect(limit > 0, "limit must be positive amount");

            var maxAmount = price * limit;

            var allowance = _allowanceMap.ContainsKey(user) ? _allowanceMap.Get<Address, BigInteger>(user) : 0;
            Runtime.Expect(allowance == 0, "unexpected pending allowance");

            allowance += maxAmount;
            _allowanceMap.Set(user, allowance);
            _allowanceTargets.Set(user, target);

            Runtime.Expect(Runtime.Nexus.TransferTokens(Runtime, Nexus.FuelTokenSymbol, user, Runtime.Chain.Address, maxAmount), "gas escrow failed");
            Runtime.Notify(EventKind.GasEscrow, user, new GasEventData() { address = target, price = price, amount = limit });
        }

        public void LoanGas(Address user, Address target, BigInteger price, BigInteger limit)
        {
            if (Runtime.readOnlyMode)
            {
                return;
            }

            Runtime.Expect(Runtime.Chain.IsRoot, "must be a root chain");

            Runtime.Expect(user.IsUser, "must be a user address");
            Runtime.Expect(target.IsSystem, "destination must be system address");
            Runtime.Expect(IsWitness(user), "invalid witness");

            Runtime.Expect(price > 0, "price must be positive amount");
            Runtime.Expect(limit > 0, "limit must be positive amount");

            var lender = FindLender();
            Runtime.Expect(!lender.IsNull, "no lender available");

            var maxAmount = price * limit;

            var allowance = _allowanceMap.ContainsKey(user) ? _allowanceMap.Get<Address, BigInteger>(user) : 0;
            Runtime.Expect(allowance == 0, "unexpected pending allowance");

            allowance += maxAmount;
            _allowanceMap.Set(user, allowance);
            _allowanceTargets.Set(user, lender);

            BigInteger lendedAmount;

            Runtime.Expect(IsLender(lender), "invalid lender address");

            Runtime.Expect(GetLoanAmount(user) == 0, "already has an active loan");

            lendedAmount = maxAmount;
            Runtime.Expect(lendedAmount <= MaxLendAmount, "limit exceeds maximum allowed for lend");

            var temp = (lendedAmount * LendReturn) / 100;
            var loan = new GasLoanEntry()
            {
                amount = temp,
                hash = Runtime.Transaction.Hash,
                borrower = user,
                lender = lender
            };
            _loanMap.Set<Address, GasLoanEntry>(user, loan);

            var list = _loanList.Get<Address, StorageList>(lender);
            list.Add<GasLoanEntry>(loan);

            Runtime.Expect(Runtime.Nexus.TransferTokens(Runtime, Nexus.FuelTokenSymbol, lender, Runtime.Chain.Address, loan.amount), "gas lend failed");
            Runtime.Notify(EventKind.GasEscrow, lender, new GasEventData() { address = target, price = price, amount = limit });
            Runtime.Notify(EventKind.GasLoan, user, new GasEventData() { address = lender, price = price, amount = limit });
        }

        public void SpendGas(Address from)
        {
            if (Runtime.readOnlyMode)
            {
                return;
            }

            Runtime.Expect(IsWitness(from), "invalid witness");

            Runtime.Expect(_allowanceMap.ContainsKey(from), "no gas allowance found");

            var availableAmount = _allowanceMap.Get<Address, BigInteger>(from);

            var spentGas = Runtime.UsedGas;
            var requiredAmount = spentGas * Runtime.GasPrice;

            Runtime.Expect(availableAmount >= requiredAmount, "gas allowance is not enough");

            /*var token = this.Runtime.Nexus.FuelToken;
            Runtime.Expect(token != null, "invalid token");
            Runtime.Expect(token.Flags.HasFlag(TokenFlags.Fungible), "must be fungible token");
            */

            var balances = new BalanceSheet(Nexus.FuelTokenSymbol);

            var leftoverAmount = availableAmount - requiredAmount;

            var targetAddress = _allowanceTargets.Get<Address, Address>(from);
            BigInteger targetGas;

            if (!targetAddress.IsNull)
            {
                targetGas = spentGas / 2; // 50% for dapps
            }
            else
            {
                targetGas = 0;
            }

            // TODO the transfers around here should pass through Nexus.TransferTokens!!
            // return unused gas to transaction creator
            if (leftoverAmount > 0)
            {
                Runtime.Expect(balances.Subtract(this.Storage, Runtime.Chain.Address, leftoverAmount), "gas leftover deposit failed");
                Runtime.Expect(balances.Add(this.Storage, from, leftoverAmount), "gas leftover withdraw failed");
            }

            if (targetGas > 0)
            {
                var targetPayment = targetGas * Runtime.GasPrice;
                Runtime.Expect(balances.Subtract(this.Storage, Runtime.Chain.Address, targetPayment), "gas target withdraw failed");
                Runtime.Expect(balances.Add(this.Storage, targetAddress, targetPayment), "gas target deposit failed");
                spentGas -= targetGas;
            }

            _allowanceMap.Remove(from);
            _allowanceTargets.Remove(from);

            // check if there is an active lend and it is time to pay it
            if (_loanMap.ContainsKey<Address>(from))
            {
                var loan = _loanMap.Get<Address, GasLoanEntry>(from);
                if (loan.hash != Runtime.Transaction.Hash)
                {
                    Runtime.Expect(_lenderMap.ContainsKey<Address>(loan.lender), "missing payment address for loan");
                    var paymentAddress = _lenderMap.Get<Address, Address>(loan.lender);
                    Runtime.Expect(Runtime.Nexus.TransferTokens(Runtime, Nexus.FuelTokenSymbol, from, paymentAddress, loan.amount), "lend payment failed");
                    _loanMap.Remove<Address>(from);

                    var list = _loanList.Get<Address, StorageList>(loan.lender);
                    int index = -1;
                    var count = list.Count();
                    for (int i=0; i<count; i++)
                    {
                        var temp = list.Get<GasLoanEntry>(i);
                        if (temp.borrower == from)
                        {
                            index = i;
                            break;
                        }
                    }

                    Runtime.Expect(index >= 0, "loan missing from list");
                    list.RemoveAt<GasLoanEntry>(index);

                    Runtime.Notify(EventKind.GasPayment, paymentAddress, new GasEventData() { address = from, price = 1, amount = loan.amount});
                }
            }

            if (targetGas > 0)
            {
                Runtime.Notify(EventKind.GasPayment, targetAddress, new GasEventData() { address = from, price = Runtime.GasPrice, amount = targetGas });
            }

            Runtime.Notify(EventKind.GasPayment, Runtime.Chain.Address, new GasEventData() { address = from, price = Runtime.GasPrice, amount = spentGas });
        }

        public Address[] GetLenders()
        {
            return _lenderList.All<Address>();
        }

        public bool IsLender(Address address)
        {
            if (address.IsUser)
            {
                return _lenderMap.ContainsKey<Address>(address);
            }

            return false;
        }

        public BigInteger GetLoanAmount(Address address)
        {
            if (_loanMap.ContainsKey<Address>(address))
            {
                var entry = _loanMap.Get<Address, GasLoanEntry>(address);
                return entry.amount;
            }

            return 0;
        }

        private Address FindLender()
        {
            var count = _lenderList.Count();
            if (count > 0)
            {
                var index = Runtime.NextRandom() % count;
                return _lenderList.Get<Address>(index);
            }

            return Address.Null;
        }

        public GasLoanEntry[] GetLoans(Address from)
        {
            var list = _loanList.Get<Address, StorageList>(from);
            return list.All<GasLoanEntry>();
        }

        public void StartLend(Address from, Address to)
        {
            Runtime.Expect(_lenderList.Count() < MaxLenderCount, "too many lenders already");
            Runtime.Expect(IsWitness(from), "invalid witness");
            Runtime.Expect(to.IsUser, "invalid destination address");
            Runtime.Expect(!IsLender(from), "already lending at source address");
            Runtime.Expect(!IsLender(to), "already lending at destination address");

            _lenderList.Add<Address>(from);
            _lenderMap.Set<Address, Address>(from, to);

            Runtime.Notify(EventKind.AddressLink, from, Runtime.Chain.Address);
        }

        public void StopLend(Address from)
        {
            Runtime.Expect(IsWitness(from), "invalid witness");
            Runtime.Expect(IsLender(from), "not a lender");

            int index = -1;
            var count = _lenderList.Count();
            for (int i = 0; i < count; i++)
            {
                var entry = _lenderList.Get<Address>(i);
                if (entry == from)
                {
                    index = i;
                    break;
                }
            }

            Runtime.Expect(index >= 0, "not lending");

            _lenderList.RemoveAt<Address>(index);
            _lenderMap.Remove<Address>(from);

            Runtime.Notify(EventKind.AddressUnlink, from, Runtime.Chain.Address);
        }
    }
}
