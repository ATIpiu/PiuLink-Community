struct PlAvOps {
  decltype(&avcodec_send_frame) send;
  decltype(&avcodec_receive_packet) receive;
  decltype(&av_packet_alloc) alloc;
  decltype(&av_packet_free) free;
  int again,eof;
};
extern "C" int pl_avcodec_encode(AVCodecContext*,const AVFrame*,void*,const PlAvOps*,bool (*)(void*,AVPacket*));
static const PlAvOps rustAvOps {avcodec_send_frame,avcodec_receive_packet,av_packet_alloc,av_packet_free,AVERROR(EAGAIN),AVERROR_EOF};
