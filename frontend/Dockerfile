# Static React console (no build step - Babel compiles the .jsx in the browser).
FROM nginx:1.27-alpine
COPY . /usr/share/nginx/html
# .jsx must be served as JavaScript for <script type="text/babel" src="...">
RUN sed -i 's#application/javascript *js;#application/javascript js jsx;#' /etc/nginx/mime.types
EXPOSE 80
