/* Include after Moonlight's Rtsp.h. Allocate in the caller's runtime so
 * freeMessage() and the Windows caller continue owning all returned memory. */
#ifdef __cplusplus
extern "C" {
#endif
uint8_t pl_setup_kind(const char *);
int pl_rtsp_parse(PRTSP_MESSAGE, const char *, int, void *(*)(size_t), void (*)(void *));
char *pl_rtsp_serialize(PRTSP_MESSAGE, int *, void *(*)(size_t));
#ifdef __cplusplus
}
#endif
