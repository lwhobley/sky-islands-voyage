import { INestApplication } from '@nestjs/common';
import { Test } from '@nestjs/testing';
import { AppModule } from './app.module';
import { AppleStoreVerifier } from './iap/apple-store.verifier';
import { PurchaseRejected } from './iap/iap.types';
import { SupabaseService } from './supabase.service';

const PLAYER = '22222222-2222-2222-2222-222222222222';

describe('HTTP API (AppModule wiring)', () => {
  let app: INestApplication;
  let base: string;
  const rpc = jest.fn();
  const appleVerify = jest.fn();

  beforeAll(async () => {
    process.env.SUPABASE_URL = 'https://example.supabase.co';
    process.env.SUPABASE_SERVICE_ROLE_KEY = 'test-key';
    const moduleRef = await Test.createTestingModule({ imports: [AppModule] })
      .overrideProvider(SupabaseService)
      .useValue({ rpc, verifyPlayerToken: async (jwt: string) => (jwt === 'good-token' ? PLAYER : null) })
      .overrideProvider(AppleStoreVerifier)
      .useValue({ verify: appleVerify })
      .compile();
    app = moduleRef.createNestApplication({ logger: false });
    await app.listen(0);
    base = await app.getUrl();
  });

  afterAll(() => app.close());
  beforeEach(() => jest.resetAllMocks());

  const verify = (body: unknown, token?: string) =>
    fetch(`${base}/iap/verify`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', ...(token ? { authorization: `Bearer ${token}` } : {}) },
      body: JSON.stringify(body),
    });

  it('serves health', async () => {
    const res = await fetch(`${base}/health`);
    expect(res.status).toBe(200);
    expect(await res.json()).toEqual({ ok: true });
  });

  it('requires a valid player token', async () => {
    expect((await verify({ store: 'apple', transactionId: '1' })).status).toBe(401);
    expect((await verify({ store: 'apple', transactionId: '1' }, 'forged')).status).toBe(401);
  });

  it('validates the body', async () => {
    expect((await verify({ store: 'steam' }, 'good-token')).status).toBe(400);
    expect((await verify({ store: 'google', productId: 'p' }, 'good-token')).status).toBe(400);
  });

  it('returns the granted player state', async () => {
    appleVerify.mockResolvedValue({ store: 'apple', productId: 'sky.rolls.small', transactionId: '2000000001', accountId: PLAYER });
    rpc.mockResolvedValue({ rolls: 200, duplicate: false });

    const res = await verify({ store: 'apple', transactionId: '2000000001' }, 'good-token');

    expect(res.status).toBe(200);
    expect(await res.json()).toEqual({ rolls: 200, duplicate: false });
    expect(rpc).toHaveBeenCalledWith('grant_purchase', expect.objectContaining({ p_user_id: PLAYER }));
  });

  it('reports rejected purchases as 422 with a reason', async () => {
    appleVerify.mockRejectedValue(new PurchaseRejected('revoked'));
    const res = await verify({ store: 'apple', transactionId: '9' }, 'good-token');
    expect(res.status).toBe(422);
    expect(await res.json()).toEqual({ error: 'revoked' });
  });

  it('reports an unconfigured store as 503', async () => {
    const res = await verify({ store: 'google', productId: 'p', purchaseToken: 't' }, 'good-token');
    expect(res.status).toBe(503);
    expect(await res.json()).toEqual({ error: 'google_not_configured' });
  });
});
