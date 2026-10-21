import { Inject, Injectable } from '@nestjs/common';
import {
  APIError,
  APIException,
  AppStoreServerAPIClient,
  Environment,
  SignedDataVerifier,
  Type,
  VerificationException,
} from '@apple/app-store-server-library';
import { APP_CONFIG, AppConfig } from '../config';
import { PurchaseRejected, StoreUnavailable, VerifiedPurchase } from './iap.types';

/**
 * Looks a StoreKit 2 transaction up with the App Store Server API and verifies Apple's
 * signature chain on the result, so a client cannot forge or replay someone else's receipt.
 */
@Injectable()
export class AppleStoreVerifier {
  private readonly api?: AppStoreServerAPIClient;
  private readonly verifier?: SignedDataVerifier;
  private readonly bundleId?: string;

  constructor(@Inject(APP_CONFIG) config: AppConfig) {
    const apple = config.apple;
    if (!apple) return;
    const environment = apple.environment === 'Production' ? Environment.PRODUCTION : Environment.SANDBOX;
    this.api = new AppStoreServerAPIClient(apple.privateKey, apple.keyId, apple.issuerId, apple.bundleId, environment);
    this.verifier = new SignedDataVerifier(apple.rootCertificates, true, environment, apple.bundleId, apple.appAppleId);
    this.bundleId = apple.bundleId;
  }

  async verify(transactionId: string): Promise<VerifiedPurchase> {
    if (!this.api || !this.verifier) throw new StoreUnavailable('apple');

    let signed: string | undefined;
    try {
      signed = (await this.api.getTransactionInfo(transactionId)).signedTransactionInfo;
    } catch (e) {
      if (e instanceof APIException && e.apiError === APIError.TRANSACTION_ID_NOT_FOUND) {
        throw new PurchaseRejected('transaction_not_found');
      }
      throw e;
    }
    if (!signed) throw new PurchaseRejected('transaction_not_found');

    let tx;
    try {
      tx = await this.verifier.verifyAndDecodeTransaction(signed);
    } catch (e) {
      if (e instanceof VerificationException) throw new PurchaseRejected('invalid_signature');
      throw e;
    }

    if (tx.bundleId !== this.bundleId) throw new PurchaseRejected('wrong_bundle');
    if (tx.type !== Type.CONSUMABLE) throw new PurchaseRejected('not_consumable');
    if (tx.revocationDate) throw new PurchaseRejected('revoked');
    if ((tx.quantity ?? 1) !== 1) throw new PurchaseRejected('unsupported_quantity');
    if (!tx.productId || !tx.transactionId) throw new PurchaseRejected('malformed_transaction');

    return {
      store: 'apple',
      productId: tx.productId,
      transactionId: tx.transactionId,
      accountId: tx.appAccountToken,
    };
  }
}
