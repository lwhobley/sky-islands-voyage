import { Controller, Get, Module } from '@nestjs/common';
import { AdminCatalogController } from './admin/admin-catalog.controller';
import { AdminController, AdminReportsController } from './admin/admin.controller';
import { AdminGuard } from './admin/admin.guard';
import { APP_CONFIG, AppConfig, loadConfig } from './config';
import { AppleStoreVerifier } from './iap/apple-store.verifier';
import { GooglePlayVerifier } from './iap/google-play.verifier';
import { IapController } from './iap/iap.controller';
import { IapService } from './iap/iap.service';
import { PlayerGuard } from './player.guard';
import { FcmPushSender, PUSH_SENDER } from './push/push.sender';
import { PushWorker } from './push/push.worker';
import { SupabaseService } from './supabase.service';

@Controller('health')
export class HealthController {
  @Get()
  health() {
    return { ok: true };
  }
}

@Module({
  controllers: [HealthController, IapController, AdminController, AdminReportsController, AdminCatalogController],
  providers: [
    { provide: APP_CONFIG, useFactory: () => loadConfig() },
    SupabaseService,
    PlayerGuard,
    AdminGuard,
    AppleStoreVerifier,
    GooglePlayVerifier,
    IapService,
    {
      provide: PUSH_SENDER,
      useFactory: (config: AppConfig) => (config.firebase ? new FcmPushSender(config.firebase) : null),
      inject: [APP_CONFIG],
    },
    PushWorker,
  ],
})
export class AppModule {}
