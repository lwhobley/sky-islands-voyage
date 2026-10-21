import { RpcError, SupabaseService } from '../supabase.service';
import { AppleStoreVerifier } from './apple-store.verifier';
import { GooglePlayVerifier } from './google-play.verifier';
import { IapService } from './iap.service';
import { PurchaseRejected, VerifiedPurchase } from './iap.types';

const PLAYER = '22222222-2222-2222-2222-222222222222';

function setup(purchase: Partial<VerifiedPurchase>, rpc: jest.Mock = jest.fn().mockResolvedValue({ rolls: 550 })) {
  const verified: VerifiedPurchase = { store: 'google', productId: 'sky.rolls.medium', transactionId: 'GPA.1', ...purchase };
  const apple = { verify: jest.fn().mockResolvedValue({ ...verified, store: 'apple' }) };
  const google = { verify: jest.fn().mockResolvedValue(verified) };
  const supabase = { rpc };
  const service = new IapService(
    apple as unknown as AppleStoreVerifier,
    google as unknown as GooglePlayVerifier,
    supabase as unknown as SupabaseService,
  );
  return { service, apple, google, rpc };
}

describe('IapService', () => {
  it('grants a verified Google purchase, then consumes it', async () => {
    const order: string[] = [];
    const finalize = jest.fn(async () => void order.push('consume'));
    const rpc = jest.fn(async () => (order.push('grant'), { rolls: 550 }));
    const { service, google } = setup({ finalize, accountId: PLAYER }, rpc);

    const state = await service.verify(PLAYER, { store: 'google', productId: 'sky.rolls.medium', purchaseToken: 'tok' });

    expect(google.verify).toHaveBeenCalledWith('sky.rolls.medium', 'tok');
    expect(rpc).toHaveBeenCalledWith('grant_purchase', {
      p_user_id: PLAYER, p_store: 'google', p_product_id: 'sky.rolls.medium', p_transaction_id: 'GPA.1',
    });
    expect(order).toEqual(['grant', 'consume']);
    expect(state).toEqual({ rolls: 550 });
  });

  it('routes Apple purchases by transaction id', async () => {
    const { service, apple, rpc } = setup({ transactionId: '2000000123' });
    await service.verify(PLAYER, { store: 'apple', transactionId: '2000000123' });
    expect(apple.verify).toHaveBeenCalledWith('2000000123');
    expect(rpc.mock.calls[0][1]).toMatchObject({ p_store: 'apple', p_transaction_id: '2000000123' });
  });

  it('rejects a receipt tagged for another player without granting', async () => {
    const { service, rpc } = setup({ accountId: '11111111-1111-1111-1111-111111111111' });
    await expect(service.verify(PLAYER, { store: 'apple', transactionId: 'x' })).rejects.toEqual(
      new PurchaseRejected('account_mismatch'),
    );
    expect(rpc).not.toHaveBeenCalled();
  });

  it('accepts account ids regardless of case', async () => {
    const { service, rpc } = setup({ accountId: PLAYER.toUpperCase() });
    await service.verify(PLAYER, { store: 'apple', transactionId: 'x' });
    expect(rpc).toHaveBeenCalled();
  });

  it('maps permanent grant errors to PurchaseRejected and does not consume', async () => {
    const finalize = jest.fn();
    const { service } = setup({ finalize }, jest.fn().mockRejectedValue(new RpcError('unknown_product', 'P0001')));
    await expect(
      service.verify(PLAYER, { store: 'google', productId: 'nope', purchaseToken: 't' }),
    ).rejects.toBeInstanceOf(PurchaseRejected);
    expect(finalize).not.toHaveBeenCalled();
  });

  it('lets transient database errors propagate (client retries)', async () => {
    const { service } = setup({}, jest.fn().mockRejectedValue(new RpcError('connection reset')));
    await expect(service.verify(PLAYER, { store: 'apple', transactionId: 'x' })).rejects.toBeInstanceOf(RpcError);
  });
});
