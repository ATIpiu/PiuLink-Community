#pragma once
#include <cstddef>
#include <cstdint>

extern "C" {
  struct PlPair;
  PlPair *pl_pair_new();
  void pl_pair_free(PlPair *);
  bool pl_pair_fresh(const PlPair *);
  bool pl_pair_begin(PlPair *, const uint8_t *salt, const char *pin, size_t pin_size);
  bool pl_pair_challenge(PlPair *, const char *encrypted, const char *cert_sig, size_t sig_size,
                         const uint8_t *random, uint8_t *response);
  bool pl_pair_proof(PlPair *, const char *encrypted, char *server_secret);
  bool pl_pair_finish(PlPair *, const char *cert_sig, size_t sig_size, const char *secret, bool signature_valid);
}
