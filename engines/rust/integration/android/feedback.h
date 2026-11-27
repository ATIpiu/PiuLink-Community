extern void pl_health_reset(void);
extern void pl_health_complete(uint32_t);
extern int pl_health_seen(uint32_t,uint64_t);
extern uint32_t pl_latest_frame(void);
typedef struct {
    bool (*stopped)(void);
    int (*fec)(void);
    bool (*send)(uint16_t,const uint8_t *,uint16_t,bool);
    void (*sleep)(uint32_t);
} PlFeedbackOps;
extern bool pl_loss_feedback(bool,bool,uint16_t,const PlFeedbackOps *);
extern bool pl_request_reference(bool,bool,uint32_t,uint32_t,uint16_t,uint16_t,const void *,uint16_t,
                                 bool (*)(uint16_t,const void *,uint16_t));
