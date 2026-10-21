import {
  ArgumentsHost,
  BadRequestException,
  Body,
  Catch,
  Controller,
  ExceptionFilter,
  HttpCode,
  HttpStatus,
  Post,
  UseFilters,
  UseGuards,
} from '@nestjs/common';
import type { Response } from 'express';
import { PlayerGuard, PlayerId } from '../player.guard';
import { IapService } from './iap.service';
import { PurchaseRejected, StoreUnavailable, VerifyRequest } from './iap.types';

@Catch(PurchaseRejected, StoreUnavailable)
class IapErrorFilter implements ExceptionFilter {
  catch(error: PurchaseRejected | StoreUnavailable, host: ArgumentsHost) {
    const status = error instanceof PurchaseRejected ? HttpStatus.UNPROCESSABLE_ENTITY : HttpStatus.SERVICE_UNAVAILABLE;
    host.switchToHttp().getResponse<Response>().status(status).json({ error: error.message });
  }
}

@Controller('iap')
@UseGuards(PlayerGuard)
@UseFilters(IapErrorFilter)
export class IapController {
  constructor(private readonly iap: IapService) {}

  /**
   * POST /iap/verify
   *   { "store": "apple", "transactionId": "..." }
   *   { "store": "google", "productId": "...", "purchaseToken": "..." }
   * 200: player state snapshot | 422: { error: reason } (don't retry) | 503: store not configured
   */
  @Post('verify')
  @HttpCode(HttpStatus.OK)
  verify(@PlayerId() playerId: string, @Body() body: unknown) {
    return this.iap.verify(playerId, parseVerifyRequest(body));
  }
}

export function parseVerifyRequest(body: unknown): VerifyRequest {
  const b = (body ?? {}) as Record<string, unknown>;
  const text = (key: string) => {
    const v = b[key];
    if (typeof v !== 'string' || v.length === 0 || v.length > 4096) throw new BadRequestException(`${key} is required`);
    return v;
  };
  if (b.store === 'apple') return { store: 'apple', transactionId: text('transactionId') };
  if (b.store === 'google') return { store: 'google', productId: text('productId'), purchaseToken: text('purchaseToken') };
  throw new BadRequestException('store must be "apple" or "google"');
}
