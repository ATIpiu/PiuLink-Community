struct PlConversion {
  void *context, *mutex, *source, *black;
  void *y_target, *y_vs, *y_ps, *uv_target, *uv_vs, *uv_ps;
  const D3D11_VIEWPORT *y, *uv, *clear_y, *clear_uv;
  uint32_t planes;
};
extern "C" int32_t pl_convert_texture(const PlConversion *);
