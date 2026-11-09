import { DoubleSide, Float32BufferAttribute, Mesh, OrthographicCamera, PlaneGeometry, Scene, ShaderMaterial, Vector2, WebGLRenderer } from 'three';

export function createSunset(host: HTMLElement, fallback: () => void): () => void {
  const canvas = document.createElement('canvas');
  const context = canvas.getContext('webgl2', { antialias: false, alpha: false, powerPreference: 'low-power' });
  if (!context) throw new Error('WebGL2 unavailable');
  const renderer = new WebGLRenderer({ canvas, context, antialias: false, alpha: false });
  renderer.setPixelRatio(Math.min(devicePixelRatio, 1.5));
  const geometry = new PlaneGeometry(2, 2);
  const material = new ShaderMaterial({
    depthWrite: false,
    uniforms: { time: { value: 0 }, resolution: { value: new Vector2() } },
    vertexShader: 'varying vec2 uvPosition; void main() { uvPosition = uv; gl_Position = vec4(position.xy, 0.0, 1.0); }',
    fragmentShader: `
      varying vec2 uvPosition;
      uniform float time;
      uniform vec2 resolution;
      float band(float bottom, float top, float y) {
        float aa = fwidth(y);
        return smoothstep(bottom-aa, bottom+aa, y) * (1.-smoothstep(top-aa, top+aa, y));
      }
      void main() {
        vec2 uv = uvPosition;
        float aspect = resolution.x / resolution.y;
        vec3 ink = vec3(.063, .035, .118);
        vec3 purple = vec3(.19, .055, .29);
        vec3 pink = vec3(1., .22, .59);
        float horizon = .34;
        vec3 color = mix(ink, purple, exp(-abs(uv.y-horizon)*5.));
        color += vec3(.015, .09, .13) * exp(-pow((uv.y-.39)*8., 2.)) * exp(-pow((uv.x-.5)*2., 2.));
        vec2 p = (uv - vec2(.5, .74)) * vec2(aspect, 1.);
        float radius = .17;
        float disc = 1. - smoothstep(radius-.001, radius+.001, length(p));
        float y = (p.y / radius + 1.) * .5;
        float stripes = smoothstep(.68-fwidth(y), .68+fwidth(y), y)
          + band(.605,.66,y) + band(.53,.585,y) + band(.45,.505,y)
          + band(.37,.42,y) + band(.29,.335,y) + band(.215,.25,y)
          + band(.145,.17,y) + band(.08,.095,y) + band(.025,.035,y);
        vec3 sun = mix(pink, vec3(1.,.30,.12), smoothstep(.1,.6,y));
        sun = mix(sun, vec3(1.,.96,.12), smoothstep(.52,.86,y));
        color += vec3(1.,.08,.25) * .22 * exp(-pow((length(p)-radius)/.022, 2.));
        color += pink * .10 * exp(-length(p)*8.);
        color = mix(color, sun, disc * clamp(stripes,0.,1.));
        if (uv.y < horizon) {
          float depth = horizon - uv.y;
          color = ink * .7;
          color += pink * .07 * exp(-depth*12.);
        }
        color += pink * .18 * exp(-abs(uv.y-horizon)*130.);
        float vignette = 1. - .42 * smoothstep(.35, .9, length((uv-.5)*vec2(.9, 1.)));
        gl_FragColor = vec4(color * vignette, 1.);
      }
    `,
  });
  const scene = new Scene();
  const sky = new Mesh(geometry, material);
  sky.frustumCulled = false;
  scene.add(sky);
  // Floor and mountains share one lattice. Extend behind the camera so its
  // moving front edge never enters the viewport; fade the distant/side edges.
  const terrain = new PlaneGeometry(240, 216, 72, 72).toNonIndexed();
  const barycentric = new Float32Array(terrain.attributes.position!.count * 3);
  for (let i = 0; i < terrain.attributes.position!.count; i++) barycentric[i * 3 + i % 3] = 1;
  terrain.setAttribute('barycentric', new Float32BufferAttribute(barycentric, 3));
  const mountains = new ShaderMaterial({
    side: DoubleSide,
    transparent: true,
    forceSinglePass: true,
    uniforms: material.uniforms,
    vertexShader: `
      attribute vec3 barycentric;
      varying vec3 edge;
      varying vec3 world;
      varying float sourceDepth;
      uniform float time;
      uniform vec2 resolution;
      float hash(vec2 p) { return fract(sin(dot(p,vec2(127.1,311.7)))*43758.5453); }
      float noise(vec2 p) {
        vec2 i=floor(p), f=fract(p); f=f*f*(3.-2.*f);
        return mix(mix(hash(i),hash(i+vec2(1.,0.)),f.x),mix(hash(i+vec2(0.,1.)),hash(i+1.),f.x),f.y);
      }
      void main() {
        float travel = time * .85;
        float z = position.y + 96. - mod(travel,3.);
        float sourceZ = z + travel;
        sourceDepth = sourceZ;
        float valley = smoothstep(6.+z*.12, 22.+z*.12, abs(position.x));
        float height = valley * (3. + 13.*noise(vec2(position.x*.09,sourceZ*.065))
          + 5.*noise(vec2(position.x*.22,sourceZ*.14)));
        world = vec3(position.x,height,z);
        edge = barycentric;
        float focal = 1.35;
        // Project onto the same 34%-high horizon as the ground grid.
        gl_Position = vec4(position.x*focal/(resolution.x/resolution.y),
          (height-1.8)*focal-.32*z, 1.002*z-.4004, z);
      }
    `,
    fragmentShader: `
      varying vec3 edge;
      varying vec3 world;
      varying float sourceDepth;
      void main() {
        vec3 aa = fwidth(edge);
        vec3 inside = smoothstep(vec3(0.),aa*1.15,edge);
        float wire = 1.-min(min(inside.x,inside.y),inside.z);
        vec2 grid = vec2(world.x/(240./72.), sourceDepth/3.);
        vec2 distanceToLine = abs(fract(grid-.5)-.5) / max(fwidth(grid),vec2(.0001));
        float lattice = 1.-min(min(distanceToLine.x,distanceToLine.y),1.);
        wire = max(lattice,wire*smoothstep(.6,4.,world.y));
        vec3 normal = normalize(cross(dFdx(world),dFdy(world)));
        float light = .5+.5*abs(dot(normal,normalize(vec3(.3,1.,.5))));
        vec3 fill = mix(vec3(.09,.025,.15),vec3(.25,.025,.20),light);
        fill = mix(vec3(.044,.021,.082),fill,smoothstep(0.,3.,world.y));
        vec3 neon = mix(vec3(.88,.10,.54),vec3(.12,.58,.85),smoothstep(2.,14.,world.y));
        vec3 color = mix(fill,neon,wire*mix(.5,.78,smoothstep(0.,3.,world.y)));
        color = mix(color,vec3(.12,.055,.20),smoothstep(65.,174.,world.z)*.8);
        float fade = (1.-smoothstep(130.,200.,world.z))*(1.-smoothstep(105.,120.,abs(world.x)));
        gl_FragColor = vec4(color,fade);
      }
    `,
  });
  const landscape = new Mesh(terrain, mountains);
  landscape.frustumCulled = false;
  landscape.renderOrder = 1;
  scene.add(landscape);
  const camera = new OrthographicCamera(-1, 1, 1, -1, 0, 1);
  renderer.domElement.className = 'sunset-canvas';
  host.appendChild(renderer.domElement);
  let disposed = false, frame = 0, previous = 0, rendered = 0, elapsed = 0;
  let sampleStart = 0, sampleFrames = 0, degraded = false;
  function resize() {
    renderer.setSize(host.clientWidth, host.clientHeight);
    material.uniforms.resolution!.value.set(host.clientWidth, host.clientHeight);
  }
  const observer = new ResizeObserver(resize);
  observer.observe(host); resize();
  function render(now: number) {
    if (disposed || document.hidden) return;
    frame = requestAnimationFrame(render);
    if (now - rendered < 1000 / 30 - 1) return;
    elapsed += previous ? Math.min((now - previous) / 1000, .1) : 0;
    previous = now; rendered = now;
    material.uniforms.time!.value = elapsed;
    renderer.render(scene, camera);
    sampleFrames++;
    if (!sampleStart) sampleStart = now;
    if (now - sampleStart >= 5000) {
      if (sampleFrames / ((now - sampleStart) / 1000) < 24) {
        if (degraded) { fallback(); return; }
        renderer.setPixelRatio(1); resize(); degraded = true;
      }
      sampleStart = now; sampleFrames = 0;
    }
  }
  function visibility() {
    cancelAnimationFrame(frame); previous = 0; rendered = 0; sampleStart = 0; sampleFrames = 0;
    if (!document.hidden && !disposed) frame = requestAnimationFrame(render);
  }
  function lost(event: Event) { event.preventDefault(); fallback(); }
  renderer.domElement.addEventListener('webglcontextlost', lost);
  document.addEventListener('visibilitychange', visibility);
  visibility();
  return () => {
    disposed = true; cancelAnimationFrame(frame); observer.disconnect();
    document.removeEventListener('visibilitychange', visibility);
    renderer.domElement.removeEventListener('webglcontextlost', lost);
    geometry.dispose(); material.dispose(); terrain.dispose(); mountains.dispose(); renderer.dispose(); renderer.forceContextLoss(); renderer.domElement.remove();
  };
}
