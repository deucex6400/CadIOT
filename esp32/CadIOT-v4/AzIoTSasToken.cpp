// Copyright (c) Microsoft Corporation. All rights reserved.
// SPDX-License-Identifier: MIT

#include "AzIoTSasToken.h"
#include <az_result.h>
#include <az_span.h>
#include <az_iot_hub_client.h>
#include <time.h>
#include <stdlib.h>

#include <mbedtls/base64.h>
#include <mbedtls/md.h>

#ifndef LogInfo
#define LogInfo(...) ((void)0)
#endif
#ifndef LogError
#define LogError(...) ((void)0)
#endif

static uint32_t get_unix_time_now()
{
  return (uint32_t)time(NULL);
}

static void hmac_sha256_sign(az_span key, az_span payload, az_span out_signature32)
{
  mbedtls_md_context_t ctx;
  mbedtls_md_type_t md_type = MBEDTLS_MD_SHA256;

  mbedtls_md_init(&ctx);
  mbedtls_md_setup(&ctx, mbedtls_md_info_from_type(md_type), 1);
  mbedtls_md_hmac_starts(&ctx, (const unsigned char*)az_span_ptr(key), az_span_size(key));
  mbedtls_md_hmac_update(&ctx, (const unsigned char*)az_span_ptr(payload), az_span_size(payload));
  mbedtls_md_hmac_finish(&ctx, (unsigned char*)az_span_ptr(out_signature32));
  mbedtls_md_free(&ctx);
}

static az_result base64_encode(az_span src, az_span dest, az_span* out)
{
  size_t out_len = 0;
  int rc = mbedtls_base64_encode((unsigned char*)az_span_ptr(dest), (size_t)az_span_size(dest), &out_len,
                                 (const unsigned char*)az_span_ptr(src), (size_t)az_span_size(src));
  if (rc != 0)
  {
    return AZ_ERROR_NOT_ENOUGH_SPACE;
  }
  *out = az_span_slice(dest, 0, (int)out_len);
  return AZ_OK;
}

AzIoTSasToken::AzIoTSasToken(
  az_iot_hub_client* client,
  az_span deviceKey,
  az_span signatureBuffer,
  az_span sasTokenBuffer)
  : client(client), deviceKey(deviceKey), signatureBuffer(signatureBuffer),
    sasTokenBuffer(sasTokenBuffer), sasToken(AZ_SPAN_EMPTY), expirationUnixTime(0)
{}

int AzIoTSasToken::Generate(unsigned int expiryTimeInMinutes)
{
  // Expiration (UNIX epoch seconds)
  uint64_t expiration = (uint64_t)get_unix_time_now() + (uint64_t)(expiryTimeInMinutes * 60);
  expirationUnixTime = (uint32_t)expiration;

  // 1) Build the string-to-sign
  uint8_t to_sign_buf[256];
  az_span to_sign = AZ_SPAN_FROM_BUFFER(to_sign_buf);
  az_span out;
  az_result res = az_iot_hub_client_sas_get_signature(client, expiration, to_sign, &out);
  if (az_result_failed(res)) { return 1; }
  to_sign = out;

  // 2) Decode base64 device key into signatureBuffer, then HMAC-SHA256 the string-to-sign
  size_t decoded_len = 0;
  int rc = mbedtls_base64_decode((unsigned char*)az_span_ptr(signatureBuffer), (size_t)az_span_size(signatureBuffer),
                                 &decoded_len,
                                 (const unsigned char*)az_span_ptr(deviceKey), (size_t)az_span_size(deviceKey));
  if (rc != 0) return 1;
  az_span decoded_key = az_span_slice(signatureBuffer, 0, (int)decoded_len);

  // HMAC-SHA256 -> first 32 bytes of signatureBuffer
  az_span hmac_out = az_span_slice(signatureBuffer, 0, 32);
  hmac_sha256_sign(decoded_key, to_sign, hmac_out);

  // 3) Base64 encode signature
  uint8_t b64_sig_buf[256];
  az_span b64_sig = AZ_SPAN_FROM_BUFFER(b64_sig_buf);
  res = base64_encode(hmac_out, b64_sig, &out);
  if (az_result_failed(res)) { return 1; }
  b64_sig = out;

  // 4) Build SAS (MQTT password) into sasTokenBuffer using official signature order
  size_t pwd_len = 0;
  res = az_iot_hub_client_sas_get_password(
      client,                      // hub client
      expiration,                  // token_expiration_epoch_time (uint64_t)
      b64_sig,                     // base64-encoded signed signature
      AZ_SPAN_EMPTY,               // key name (not used for device-level SAS)
      (char*)az_span_ptr(sasTokenBuffer),   // out_password buffer
      (size_t)az_span_size(sasTokenBuffer), // buffer size
      &pwd_len);                           // out length
  if (az_result_failed(res)) { return 1; }

  // Store the resulting token span
  sasToken = az_span_slice(sasTokenBuffer, 0, (int)pwd_len);
  return 0;
}

bool AzIoTSasToken::IsExpired()
{
  return get_unix_time_now() >= expirationUnixTime;
}

az_span AzIoTSasToken::Get()
{
  return sasToken;
}
