// An orthographic ray/sphere intersection renders exactly one textured disc.
const vertex = `attribute vec2 position;
varying vec2 point;
void main() { point = position; gl_Position = vec4(position, 0., 1.); }`;

const fragment = `precision highp float;
varying vec2 point;
uniform sampler2D surface;
uniform sampler2D elevation;
uniform vec2 rotation;
const float PI = 3.14159265;
void main() {
  float radial = dot(point, point);
  if (radial > 1.0) discard;
  vec3 n = vec3(point, sqrt(max(0., 1.0 - radial)));
  float cp = cos(rotation.y), sp = sin(rotation.y);
  vec3 p = vec3(n.x, cp*n.y + sp*n.z, cp*n.z - sp*n.y);
  vec2 uv = vec2(fract(.5 + atan(p.x,p.z)/(2.*PI) + rotation.x), .5 - asin(clamp(p.y,-1.,1.))/PI);
  float base = dot(texture2D(surface, uv).rgb, vec3(.32,.53,.15));
  float h = texture2D(elevation,uv).r;
  float dx = texture2D(elevation, uv + vec2(.00098,0.)).r - h;
  float dy = texture2D(elevation, uv + vec2(0.,.00195)).r - h;
  vec3 lamp = normalize(vec3(-.76,.63,.34));
  float diffuse = dot(n,lamp);
  float relief = clamp(dx*2.2 + dy*1.9,-.21,.22);
  float lit = pow(max(0., diffuse + relief), .57);
  float albedo = clamp((base-.48)*1.65+.45, .13,1.0);
  float rim = pow(1.-n.z, 4.) * (.08 + .17*max(0.,diffuse));
  vec3 col = vec3(.026,.035,.041) + albedo*(.04+lit*.96)*vec3(1.0,.992,.958) + rim*vec3(.69,.85,.91);
  gl_FragColor = vec4(col, 1.);
}`;

export async function createMoonRenderer(canvas, assetBase) {
  const gl = canvas.getContext("webgl", {
    alpha: true,
    antialias: false,
    depth: false,
    powerPreference: "low-power",
  });
  if (!gl) return null;
  const shaders = [],
    textures = [];
  const program = gl.createProgram();
  const buffer = gl.createBuffer();
  function dispose() {
    textures.forEach((texture) => gl.deleteTexture(texture));
    shaders.forEach((shader) => gl.deleteShader(shader));
    gl.deleteBuffer(buffer);
    gl.deleteProgram(program);
  }
  try {
    for (const [type, source] of [
      [gl.VERTEX_SHADER, vertex],
      [gl.FRAGMENT_SHADER, fragment],
    ]) {
      const shader = gl.createShader(type);
      shaders.push(shader);
      gl.shaderSource(shader, source);
      gl.compileShader(shader);
      if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS))
        throw new Error("Moon shader did not compile");
      gl.attachShader(program, shader);
    }
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS))
      throw new Error("Moon renderer unavailable");
    const maps = await Promise.all(
      ["moon-color.jpg", "moon-height.jpg"].map(async (file) => {
        const bitmap = new Image();
        bitmap.src = assetBase + file;
        await bitmap.decode();
        return bitmap;
      }),
    );
    gl.useProgram(program);
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
    gl.bufferData(
      gl.ARRAY_BUFFER,
      new Float32Array([-1, -1, 3, -1, -1, 3]),
      gl.STATIC_DRAW,
    );
    const index = gl.getAttribLocation(program, "position");
    gl.enableVertexAttribArray(index);
    gl.vertexAttribPointer(index, 2, gl.FLOAT, false, 0, 0);
    maps.forEach((bitmap, unit) => {
      gl.activeTexture(gl.TEXTURE0 + unit);
      const texture = gl.createTexture();
      textures.push(texture);
      gl.bindTexture(gl.TEXTURE_2D, texture);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGB, gl.RGB, gl.UNSIGNED_BYTE, bitmap);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.REPEAT);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      gl.uniform1i(
        gl.getUniformLocation(program, unit ? "elevation" : "surface"),
        unit,
      );
    });
    const rotation = gl.getUniformLocation(program, "rotation");
    return {
      dispose,
      draw(longitude, latitude) {
        const side = Math.max(
          1,
          Math.round(
            canvas.clientWidth * Math.min(window.devicePixelRatio || 1, 1.65),
          ),
        );
        if (canvas.width !== side) {
          canvas.width = side;
          canvas.height = side;
        }
        gl.viewport(0, 0, side, side);
        gl.uniform2f(rotation, longitude, latitude);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
      },
    };
  } catch {
    dispose();
    return null;
  }
}
