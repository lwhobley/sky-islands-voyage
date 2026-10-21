export type Store = 'apple' | 'google';

export type VerifyRequest =
  | { store: 'apple'; transactionId: string }
  | { store: 'google'; productId: string; purchaseToken: string };

/** A purchase the store has confirmed as paid and genuine. */
export interface VerifiedPurchase {
  store: Store;
  productId: string;
  /** Apple transactionId / Google orderId — the idempotency key in public.purchases. */
  transactionId: string;
  /** appAccountToken (Apple) / obfuscatedExternalAccountId (Google), if the client set it. */
  accountId?: string;
  /** Runs after the grant is committed (Google: consume so the item can be bought again). */
  finalize?: () => Promise<void>;
}

/** The purchase is not valid for this player; the client should not retry. */
export class PurchaseRejected extends Error {
  constructor(readonly reason: string) {
    super(reason);
  }
}

/** The store integration is not configured on this server. */
export class StoreUnavailable extends Error {
  constructor(readonly store: Store) {
    super(`${store}_not_configured`);
  }
}
