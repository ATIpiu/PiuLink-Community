static void* createOpusBackend(const PlOpusFormat* f, int* error) {
    return opus_multistream_decoder_create(f->rate, f->channels, f->streams, f->coupled, f->mapping, error);
}
static int decodeOpusBackend(void* handle,const uint8_t* packet,int len,int16_t* pcm,int samples,int fec) {
    return opus_multistream_decode(handle,packet,len,pcm,samples,fec);
}
static void destroyOpusBackend(void* handle) { opus_multistream_decoder_destroy(handle); }
static const PlDecoderOps opusBackend = {createOpusBackend, decodeOpusBackend, destroyOpusBackend};
