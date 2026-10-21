import { Inject, Injectable } from '@nestjs/common';
import { createHash } from 'node:crypto';
import { GoogleAuth } from 'google-auth-library';
import { APP_CONFIG, AppConfig } from '../config';
import { PurchaseRejected, StoreUnavailable, VerifiedPurchase } from './iap.types';

const API = 'https://androidpublisher.googleapis.com/androidpublisher/v3/applications';

/** Subset of the Play Developer API ProductPurchase resource. */
interface ProductPurchase {
  purchaseState?: number; // 0 purchased, 1 cancelled, 2 pending
  consumptionState?: number; // 0 not yet consumed, 1 consumed
  orderId?: string;
  obfuscatedExternalAccountId?: string;
}

/** Verifies one-time (consumable) products with the Google Play Developer API. */
@Injectable()
export class GooglePlayVerifier {
  private readonly auth?: GoogleAuth;
  private readonly packageName?: string;

  constructor(@Inject(APP_CONFIG) config: AppConfig) {
    if (!config.google) return;
    this.auth = new GoogleAuth({
      keyFile: config.google.serviceAccountKeyFile,
      scopes: ['https://www.googleapis.com/auth/androidpublisher'],
    });
    this.packageName = config.google.packageName;
  }

  async verify(productId: string, purchaseToken: string): Promise<VerifiedPurchase> {
    if (!this.auth || !this.packageName) throw new StoreUnavailable('google');
    const url = `${API}/${encodeURIComponent(this.packageName)}/purchases/products/` +
      `${encodeURIComponent(productId)}/tokens/${encodeURIComponent(purchaseToken)}`;

    let purchase: ProductPurchase;
    try {
      purchase = (await this.request<ProductPurchase>(url, 'GET')).data;
    } catch (e) {
      const status = (e as { response?: { status?: number } }).response?.status;
      if (status === 400 || status === 404 || status === 410) throw new PurchaseRejected('purchase_not_found');
      throw e;
    }

    if (purchase.purchaseState === 2) throw new PurchaseRejected('purchase_pending');
    if (purchase.purchaseState !== 0) throw new PurchaseRejected('purchase_cancelled');

    return {
      store: 'google',
      productId,
      // License-tester purchases may lack an orderId; fall back to a stable token hash.
      transactionId: purchase.orderId ?? `token:${createHash('sha256').update(purchaseToken).digest('hex')}`,
      accountId: purchase.obfuscatedExternalAccountId,
      finalize: purchase.consumptionState === 0 ? () => this.request(`${url}:consume`, 'POST').then(() => undefined) : undefined,
    };
  }

  private async request<T>(url: string, method: 'GET' | 'POST') {
    const client = await this.auth!.getClient();
    return client.request<T>({ url, method });
  }
}
