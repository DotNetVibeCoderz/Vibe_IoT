# MAVLink dialects

`minimal.xml`, `standard.xml` and `common.xml` are the official MAVLink message definitions from
https://github.com/mavlink/mavlink (`message_definitions/v1.0`), MIT-licensed (see https://mavlink.io/en/#license).
They are compiled into this package by the IoTCom.Net MAVLink source generator. To update, copy newer files here;
`/conformance/mavlink*.json` are regenerated from them by `python conformance/generate.py`.
