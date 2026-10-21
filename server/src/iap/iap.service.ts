import { Injectable } from '@nestjs/common';
import { RpcError, SupabaseService } from '../supabase.service';
import { AppleStoreVerifier } from './apple-store.verifier';
import { GooglePlayVerifier } from './google-play.verifier';
import { PurchaseRejected, VerifiedPurchase, VerifyRequest } from './iap.types';

/** Grant failures that mean "this purchase is not yours / not sellable", not "try again". */
const PERMANENT_GRANT_ERRORS = new Set(['unknown_product', 'transaction_belongs_to_another_player']);

@Injectable()
export class IapService {
  constructor(
    private readonly apple: AppleStoreVerifier,
    private readonly google: GooglePlayVerifier,
    private readonly supabase: SupabaseService,
  ) {}

  /**
   * Verifies a store purchase and grants its contents exactly once.
   * Returns the player-state snapshot (same shape as every game RPC) for the client to apply.
   */
  async verify(playerId: string, request: VerifyRequest): Promise<unknown> {
    const purchase: VerifiedPurchase =
      request.store === 'apple'
        ? await this.apple.verify(request.transactionId)
        : await this.google.verify(request.productId, request.purchaseToken);

    // The client tags each purchase with the player's id; reject receipts bought for someone else.
    if (purchase.accountId && purchase.accountId.toLowerCase() !== playerId.toLowerCase()) {
      throw new PurchaseRejected('account_mismatch');
    }

    let state: unknown;
    try {
      state = await this.supabase.rpc('grant_purchase', {
        p_user_id: playerId,
        p_store: purchase.store,
        p_product_id: purchase.productId,
        p_transaction_id: purchase.transactionId,
      });
    } catch (e) {
      if (e instanceof RpcError && PERMANENT_GRANT_ERRORS.has(e.message)) throw new PurchaseRejected(e.message);
      throw e;
    }

    // Only after the grant is durable; if this fails, a retry re-grants as a no-op and consumes again.
    await purchase.finalize?.();
    return state;
  }
}
