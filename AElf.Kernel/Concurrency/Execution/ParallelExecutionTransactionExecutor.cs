﻿using System;
using System.Collections.Generic;
using Akka.Actor;

namespace AElf.Kernel.Concurrency.Execution
{
	public class ParallelExecutionTransactionExecutor : UntypedActor
	{
		private IChainContext _chainContext;
		public ParallelExecutionTransactionExecutor(IChainContext chainContext)
		{
			_chainContext = chainContext;
		}

		protected override void OnReceive(object message)
		{
			// TODO: Implement
			throw new System.NotImplementedException();
		}

		public static Props Props(IChainContext chainContext)
		{
			return Akka.Actor.Props.Create(() => new ParallelExecutionTransactionExecutor(chainContext));
		}

	}
}